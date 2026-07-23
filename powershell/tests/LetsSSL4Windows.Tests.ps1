#requires -Version 5.1
<#
    Pester v5 tests for LetsSSL4Windows (PowerShell edition).

    These cover the deterministic, side-effect-free logic - data model, status
    computation, the JSON store, settings, selectors and (on Windows) the DPAPI
    secret round-trip. ACME issuance / IIS / scheduled-task functions are not
    exercised here because they require a live Windows + Let's Encrypt + IIS
    environment.

    Run:
        Install-Module Pester -MinimumVersion 5.0 -Scope CurrentUser   # once
        Invoke-Pester -Path .\tests\LetsSSL4Windows.Tests.ps1

    The store is redirected into Pester's TestDrive via $env:ProgramData, so the
    real %ProgramData%\LetsSSL4Windows store is never touched.
#>

BeforeAll {
    $script:OrigProgramData    = $env:ProgramData
    $env:ProgramData           = "$TestDrive"
    $env:LETSSSL4WINDOWS_NORUN = '1'

    . (Join-Path $PSScriptRoot '..\LetsSSL4Windows.ps1')
    Initialize-Paths

    $script:StoreRoot    = Join-Path $env:ProgramData 'LetsSSL4Windows'
    $script:CertsPath    = Join-Path $StoreRoot 'certificates.json'
    $script:SettingsPath = Join-Path $StoreRoot 'appsettings.json'
    $script:IsWindowsOS  = ($env:OS -eq 'Windows_NT')

    function New-TestCert {
        param([string]$Domain = 'www.example.com', [string]$Name)
        $c = New-ManagedCertificate
        $c.PrimaryDomain = $Domain
        $c.Name = if ($Name) { $Name } else { $Domain }
        return $c
    }
}

AfterAll {
    $env:ProgramData = $script:OrigProgramData
    Remove-Item Env:LETSSSL4WINDOWS_NORUN -ErrorAction SilentlyContinue
}

Describe 'Enum maps (parity with the C# model)' {
    It 'uses the expected numeric values' {
        $Script:Env_Staging      | Should -Be 0
        $Script:Env_Production   | Should -Be 1
        $Script:Ch_Http01        | Should -Be 0
        $Script:Ch_Dns01         | Should -Be 1
        $Script:Dns_Manual       | Should -Be 0
        $Script:Dns_Cloudflare   | Should -Be 1
        $Script:Dep_ExportPfx    | Should -Be 0
        $Script:Dep_ExportPem    | Should -Be 1
        $Script:Dep_RunScript    | Should -Be 2
    }
}

Describe 'New-ManagedCertificate' {
    It 'sets sensible defaults' {
        $c = New-ManagedCertificate
        $c.ChallengeType           | Should -Be 0
        $c.DnsProvider             | Should -Be 0
        $c.BindToIis               | Should -BeTrue
        $c.AutoRenew               | Should -BeTrue
        $c.RenewalDaysBeforeExpiry | Should -Be 30
        $c.Thumbprint              | Should -BeNullOrEmpty
        @($c.RemoteTargets).Count  | Should -Be 0
    }
    It 'produces a 32-char hex Id' {
        (New-ManagedCertificate).Id | Should -Match '^[0-9a-f]{32}$'
    }
    It 'produces unique Ids' {
        (New-ManagedCertificate).Id | Should -Not -Be (New-ManagedCertificate).Id
    }
}

Describe 'Remote IIS targets' {
    It 'New-RemoteIisTarget defaults to WinRM over HTTPS' {
        $t = New-RemoteIisTarget -HostName 'web2.corp'
        $t.Host       | Should -Be 'web2.corp'
        $t.WinRmPort  | Should -Be 5986
        $t.UseSsl     | Should -BeTrue
        @($t.SiteNames).Count | Should -Be 0
    }

    It 'ConvertTo-RemoteIisTarget parses a full spec' {
        $t = ConvertTo-RemoteIisTarget -Spec 'host=web2;sites=Default Web Site,api;port=5985;ssl=0'
        $t.Host      | Should -Be 'web2'
        $t.WinRmPort | Should -Be 5985
        $t.UseSsl    | Should -BeFalse
        $t.SiteNames | Should -Be @('Default Web Site', 'api')
    }

    It 'ConvertTo-RemoteIisTarget applies defaults for omitted fields' {
        $t = ConvertTo-RemoteIisTarget -Spec 'host=web3'
        $t.WinRmPort | Should -Be 5986
        $t.UseSsl    | Should -BeTrue
        @($t.SiteNames).Count | Should -Be 0
    }

    It 'ConvertTo-RemoteIisTarget requires a host' {
        { ConvertTo-RemoteIisTarget -Spec 'sites=api' } | Should -Throw
    }

    It 'round-trips through the JSON store' {
        $c = New-TestCert -Domain 'store.example.com'
        $c.RemoteTargets = @(New-RemoteIisTarget -HostName 'web9' -WinRmPort 5985 -UseSsl $false -SiteNames @('api'))
        Set-Certificate -Certificate $c
        $reloaded = Resolve-Certificate -Selector $c.Id
        @($reloaded.RemoteTargets).Count | Should -Be 1
        $reloaded.RemoteTargets[0].Host      | Should -Be 'web9'
        $reloaded.RemoteTargets[0].WinRmPort | Should -Be 5985
        $reloaded.RemoteTargets[0].UseSsl    | Should -BeFalse
    }
}

Describe 'Get-AllDomains' {
    It 'returns the primary first' {
        $c = New-TestCert -Domain 'www.example.com'
        $c.SubjectAlternativeNames = @('example.com', 'shop.example.com')
        (Get-AllDomains -Cert $c)[0] | Should -Be 'www.example.com'
    }
    It 'includes the SANs' {
        $c = New-TestCert -Domain 'www.example.com'
        $c.SubjectAlternativeNames = @('example.com')
        (Get-AllDomains -Cert $c) | Should -Contain 'example.com'
    }
    It 'de-duplicates (case-insensitive) and trims whitespace' {
        $c = New-TestCert -Domain 'www.example.com'
        $c.SubjectAlternativeNames = @(' WWW.example.com ', 'example.com', '')
        $all = Get-AllDomains -Cert $c
        @($all | Where-Object { $_ -ieq 'www.example.com' }).Count | Should -Be 1
        $all | Should -Contain 'example.com'
        $all | Should -Not -Contain ''
    }
}

Describe 'Get-CertStatus' {
    It 'is NotRequested with no expiry and no error' {
        Get-CertStatus -Cert (New-TestCert) | Should -Be $Script:St_NotRequested
    }
    It 'is Error when LastError set and never issued' {
        $c = New-TestCert; $c.LastError = 'boom'
        Get-CertStatus -Cert $c | Should -Be $Script:St_Error
    }
    It 'is Valid when expiry is far in the future' {
        $c = New-TestCert; $c.NotAfter = (Get-Date).AddDays(60).ToUniversalTime().ToString('o')
        Get-CertStatus -Cert $c | Should -Be $Script:St_Valid
    }
    It 'is ExpiringSoon inside the renewal window' {
        $c = New-TestCert; $c.NotAfter = (Get-Date).AddDays(10).ToUniversalTime().ToString('o')
        Get-CertStatus -Cert $c | Should -Be $Script:St_ExpiringSoon
    }
    It 'is Expired when past the expiry date' {
        $c = New-TestCert; $c.NotAfter = (Get-Date).AddDays(-1).ToUniversalTime().ToString('o')
        Get-CertStatus -Cert $c | Should -Be $Script:St_Expired
    }
}

Describe 'Test-IsDueForRenewal' {
    It 'is false when auto-renew is off, even if expired' {
        $c = New-TestCert; $c.AutoRenew = $false
        $c.NotAfter = (Get-Date).AddDays(-1).ToUniversalTime().ToString('o')
        Test-IsDueForRenewal -Cert $c | Should -BeFalse
    }
    It 'is true for a never-issued auto-renew cert' {
        Test-IsDueForRenewal -Cert (New-TestCert) | Should -BeTrue
    }
    It 'is false for a healthy, valid cert' {
        $c = New-TestCert; $c.NotAfter = (Get-Date).AddDays(60).ToUniversalTime().ToString('o')
        Test-IsDueForRenewal -Cert $c | Should -BeFalse
    }
    It 'is true when expiring soon' {
        $c = New-TestCert; $c.NotAfter = (Get-Date).AddDays(5).ToUniversalTime().ToString('o')
        Test-IsDueForRenewal -Cert $c | Should -BeTrue
    }
    It 'is true when the ARI-suggested time has passed, even if date-wise valid' {
        $c = New-TestCert
        $c.NotAfter = (Get-Date).AddDays(60).ToUniversalTime().ToString('o')
        $c.AriRenewalTime = [datetimeoffset]::UtcNow.AddMinutes(-5).ToString('o')
        Test-IsDueForRenewal -Cert $c | Should -BeTrue
    }
    It 'ignores a future ARI-suggested time on a valid cert' {
        $c = New-TestCert
        $c.NotAfter = (Get-Date).AddDays(60).ToUniversalTime().ToString('o')
        $c.AriRenewalTime = [datetimeoffset]::UtcNow.AddDays(40).ToString('o')
        Test-IsDueForRenewal -Cert $c | Should -BeFalse
    }
}

Describe 'ACME Renewal Information (ARI, RFC 9773)' {
    It 'computes the CertID from the RFC 9773 worked example' {
        # AKI keyIdentifier and serial from RFC 9773 §4.1.
        $aki = [byte[]]@(
            0x69, 0x88, 0x5b, 0x6b, 0x87, 0x46, 0x40, 0x41, 0xe1, 0xb3,
            0x7b, 0x84, 0x7b, 0xa0, 0xae, 0x2c, 0xde, 0x01, 0xc8, 0xd4)
        $serial = [byte[]]@(0x00, 0x87, 0x65, 0x43, 0x21)
        Get-AriCertIdFromParts -KeyIdentifier $aki -Serial $serial |
            Should -Be 'aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE'
    }
    It 'produces url-safe, unpadded base64' {
        $b = [byte[]]@(0xfb, 0xff, 0xbf)
        $id = Get-AriCertIdFromParts -KeyIdentifier $b -Serial $b
        $id | Should -Be '-_-_.-_-_'
        $id | Should -Not -Match '[+/=]'
    }
    It 'parses the keyIdentifier out of an AKI extension DER' {
        # SEQUENCE { [0] keyIdentifier = 01 02 03 04 }
        $der = [byte[]]@(0x30, 0x06, 0x80, 0x04, 0x01, 0x02, 0x03, 0x04)
        $keyId = Get-AriKeyIdentifier -AkiRawData $der
        (([byte[]]$keyId) -join ',') | Should -Be '1,2,3,4'
    }
}

Describe 'Get-StatusText' {
    It 'maps <value> to <text>' -ForEach @(
        @{ Value = 0; Text = 'Not requested' }
        @{ Value = 1; Text = 'Valid' }
        @{ Value = 2; Text = 'Expiring soon' }
        @{ Value = 3; Text = 'Expired' }
        @{ Value = 4; Text = 'Error' }
    ) {
        Get-StatusText -Status $Value | Should -Be $Text
    }
}

Describe 'Get-PropValue' {
    It 'returns an existing property value' {
        $o = [pscustomobject]@{ Path = 'C:\x' }
        Get-PropValue -Obj $o -Name 'Path' | Should -Be 'C:\x'
    }
    It 'returns the default for a missing property' {
        $o = [pscustomobject]@{ Path = 'C:\x' }
        Get-PropValue -Obj $o -Name 'Password' -Default 'd' | Should -Be 'd'
    }
    It 'returns the default for a null object' {
        Get-PropValue -Obj $null -Name 'Anything' -Default 'd' | Should -Be 'd'
    }
}

Describe 'New-RandomPassword' {
    It 'returns a string of the requested length' {
        (New-RandomPassword -Length 24).Length | Should -Be 24
    }
    It 'returns different values each call' {
        (New-RandomPassword) | Should -Not -Be (New-RandomPassword)
    }
}

Describe 'DPAPI secret protection' -Skip:(-not ($env:OS -eq 'Windows_NT')) {
    It 'round-trips a secret' {
        $secret = 'cf-token-12345'
        Unprotect-Secret -Stored (Protect-Secret -Plaintext $secret) | Should -Be $secret
    }
    It 'prefixes protected values with DPAPI:' {
        (Protect-Secret -Plaintext 'x').StartsWith('DPAPI:') | Should -BeTrue
    }
    It 'passes through empty input' {
        Protect-Secret -Plaintext '' | Should -BeNullOrEmpty
    }
    It 'returns non-DPAPI strings unchanged on unprotect' {
        Unprotect-Secret -Stored 'plain' | Should -Be 'plain'
    }
}

Describe 'Certificate store (JSON)' {
    BeforeEach { Save-AllCertificates -Certificates @() }

    It 'persists and reloads a certificate' {
        Set-Certificate -Certificate (New-TestCert -Domain 'a.example.com')
        @(Get-AllCertificates).Count | Should -Be 1
    }
    It 'writes a JSON array even for a single certificate' {
        Set-Certificate -Certificate (New-TestCert -Domain 'a.example.com')
        (Get-Content -LiteralPath $CertsPath -Raw).TrimStart() | Should -Match '^\['
    }
    It 'updates (not duplicates) an existing certificate by Id' {
        $c = New-TestCert -Domain 'a.example.com'
        Set-Certificate -Certificate $c
        $c.Name = 'renamed'
        Set-Certificate -Certificate $c
        $all = Get-AllCertificates
        $all.Count    | Should -Be 1
        $all[0].Name  | Should -Be 'renamed'
    }
    It 'resolves by exact Id' {
        $c = New-TestCert -Domain 'a.example.com'
        Set-Certificate -Certificate $c
        (Resolve-Certificate -Selector $c.Id).Id | Should -Be $c.Id
    }
    It 'resolves by a unique domain fragment' {
        Set-Certificate -Certificate (New-TestCert -Domain 'unique.example.com')
        (Resolve-Certificate -Selector 'unique').PrimaryDomain | Should -Be 'unique.example.com'
    }
    It 'throws on an ambiguous selector' {
        Set-Certificate -Certificate (New-TestCert -Domain 'a.example.com')
        Set-Certificate -Certificate (New-TestCert -Domain 'b.example.com')
        { Resolve-Certificate -Selector 'example.com' } | Should -Throw
    }
    It 'throws when nothing matches' {
        { Resolve-Certificate -Selector 'nope' } | Should -Throw
    }
    It 'removes a certificate' {
        $c = New-TestCert -Domain 'a.example.com'
        Set-Certificate -Certificate $c
        Remove-Certificate -CertId $c.Id
        @(Get-AllCertificates).Count | Should -Be 0
    }
}

Describe 'Get-CertDnsNames' -Skip:(-not ($env:OS -eq 'Windows_NT')) {
    It 'extracts the CN first, then SAN DNS names' {
        $rsa = [System.Security.Cryptography.RSA]::Create(2048)
        $req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=www.example.com', $rsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $san.AddDnsName('www.example.com'); $san.AddDnsName('example.com')
        $req.CertificateExtensions.Add($san.Build())
        $cert = $req.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(89))

        $names = @(Get-CertDnsNames -Cert $cert)
        $names[0] | Should -Be 'www.example.com'
        $names    | Should -Contain 'example.com'
    }
}

Describe 'Settings' {
    BeforeEach { if (Test-Path $SettingsPath) { Remove-Item $SettingsPath -Force } }

    It 'returns defaults when no file exists' {
        $s = Get-Settings
        $s.Environment       | Should -Be $Script:Env_Staging
        $s.EnableAutoRenewal | Should -BeTrue
        $s.Notifications      | Should -Not -BeNullOrEmpty
    }
    It 'round-trips a saved change' {
        $s = Get-Settings
        $s.Environment = $Script:Env_Production
        Save-Settings $s
        (Get-Settings).Environment | Should -Be $Script:Env_Production
    }
    It 'back-fills missing members from defaults' {
        Set-Content -LiteralPath $SettingsPath -Value '{ "ContactEmail": "x@y.com" }' -Encoding UTF8
        $s = Get-Settings
        $s.ContactEmail            | Should -Be 'x@y.com'
        $s.Notifications           | Should -Not -BeNullOrEmpty
        $s.Notifications.NotifyOnFailure | Should -BeTrue
    }
}
