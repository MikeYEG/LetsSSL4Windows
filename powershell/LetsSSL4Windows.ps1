<#
.SYNOPSIS
    LetsSSL4Windows (PowerShell edition) - a free, console-driven Let's Encrypt
    certificate manager for Windows / IIS.

.DESCRIPTION
    A single, self-contained PowerShell port of the LetsSSL4Windows desktop app.
    It requests, installs, binds, deploys, and auto-renews Let's Encrypt
    certificates through the ACME protocol (RFC 8555) using the MIT-licensed
    Posh-ACME module - all from the PowerShell console.

    Run it with NO arguments for an interactive menu, or pass -Command <verb>
    to drive a single action non-interactively (ideal for scheduled renewal).

    Features (parity with the WPF app):
      * Let's Encrypt issuance over ACME (staging + production)
      * HTTP-01 and DNS-01 validation, including wildcard certificates
      * DNS providers: Cloudflare (automated) and Manual
      * Install into LocalMachine\My and auto-bind to IIS with SNI
      * Deployment tasks: export PFX, export PEM, run a post-issue script
      * On-demand export (PFX / PEM)
      * Email (SMTP) and webhook notifications on success/failure
      * Encrypted secrets at rest with Windows DPAPI (LocalMachine scope)
      * Unattended renewal via a Windows Scheduled Task

    Data is stored under %ProgramData%\LetsSSL4Windows, using the SAME JSON
    schema as the C# app, so the two can share one store.

.PARAMETER Command
    Non-interactive verb. One of:
      List, New, Renew, RenewDue, Bind, Export, Remove, Show,
      Settings, InstallTask, UninstallTask, Help
    Omit it to launch the interactive menu.

.PARAMETER Id
    Certificate Id (or unique Name/PrimaryDomain prefix) for Renew/Bind/Export/Remove/Show.

.EXAMPLE
    .\LetsSSL4Windows.ps1
    Launch the interactive console menu.

.EXAMPLE
    .\LetsSSL4Windows.ps1 -Command RenewDue
    Renew every certificate currently due (used by the scheduled task).

.EXAMPLE
    .\LetsSSL4Windows.ps1 -Command New -Domain www.example.com -SAN example.com -ChallengeType Http01 -IisSite "Default Web Site"
    Request a certificate non-interactively over HTTP-01 and bind it to IIS.

.NOTES
    Requires Windows PowerShell 5.1+ or PowerShell 7+, run as Administrator for
    cert-store / IIS / scheduled-task operations. Posh-ACME is installed
    automatically (CurrentUser scope) if missing.
    MIT License (c) MikeYEG and contributors.
#>

[CmdletBinding()]
param(
    [ValidateSet('List','New','Renew','RenewDue','Bind','Export','Remove','Show',
                 'Import','Settings','InstallTask','UninstallTask','Help','Menu')]
    [string]$Command = 'Menu',

    # --- Selection ---
    [string]$Id,

    # --- New certificate ---
    [string]$Name,
    [string]$Domain,
    [string[]]$SAN,
    [string]$ContactEmail,
    [ValidateSet('Http01','Dns01')]
    [string]$ChallengeType,
    [ValidateSet('Manual','Cloudflare')]
    [string]$DnsProvider,
    [string]$DnsCredential,          # e.g. a Cloudflare API token
    [string]$IisSite,
    [string]$WebRoot,
    [string]$FriendlyName,           # name shown for the certificate in IIS
    [string[]]$RemoteTarget,         # remote IIS server(s): "host=web2;sites=Default Web Site,api;port=5986;ssl=1"
    [switch]$NoBind,
    [switch]$NoAutoRenew,
    [int]$RenewalDays = 30,

    # --- Settings ---
    [ValidateSet('Staging','Production')]
    [string]$Environment,

    # --- Export ---
    [ValidateSet('Pfx','Pem')]
    [string]$ExportType = 'Pfx',
    [string]$OutPath,
    [string]$PfxPassword,

    # --- Import / rescan ---
    [switch]$IncludeNonLetsEncrypt,

    # Used internally by the scheduled task to find this script.
    [switch]$Unattended
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#region ----------------------------------------------------------- Constants

$Script:AppName       = 'LetsSSL4Windows'
$Script:ScriptPath    = $MyInvocation.MyCommand.Path
# Captured at load so self-elevation can faithfully pass the same args through.
$Script:BoundParams   = $PSBoundParameters
$Script:RootDir       = Join-Path $env:ProgramData $AppName
$Script:PfxDir        = Join-Path $RootDir 'pfx'
$Script:LogsDir       = Join-Path $RootDir 'logs'
$Script:PoshAcmeHome  = Join-Path $RootDir 'posh-acme'
$Script:SettingsFile  = Join-Path $RootDir 'appsettings.json'
$Script:CertsFile     = Join-Path $RootDir 'certificates.json'
$Script:LastRunFile   = Join-Path $RootDir 'lastrun.json'
$Script:TaskName      = 'LetsSSL4Windows Renewal'

# Windows Event Log (Event Viewer) target. Entries appear under Windows Logs >
# Application with this source. $EventSourceReady is resolved lazily on first use.
$Script:EventLogName    = 'Application'
$Script:EventLogSource  = 'LetsSSL4Windows'
$Script:EventSourceReady = $null

# Enum maps mirror the C# app's numeric JSON serialization for interop.
$Script:Env_Staging = 0; $Script:Env_Production = 1
$Script:Ch_Http01   = 0; $Script:Ch_Dns01      = 1
$Script:Dns_Manual  = 0; $Script:Dns_Cloudflare = 1
$Script:Dep_ExportPfx = 0; $Script:Dep_ExportPem = 1; $Script:Dep_RunScript = 2
$Script:St_NotRequested = 0; $Script:St_Valid = 1; $Script:St_ExpiringSoon = 2
$Script:St_Expired = 3; $Script:St_Error = 4

#endregion

#region ----------------------------------------------------------- Infrastructure

function Initialize-Paths {
    foreach ($d in @($RootDir, $PfxDir, $LogsDir, $PoshAcmeHome)) {
        if (-not (Test-Path -LiteralPath $d)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
        }
    }
}

function Initialize-EventLogSource {
    # Registers the Event Log source once (needs admin). Cached so it's attempted
    # only once per run; returns $true when writing to the event log is possible.
    if ($null -ne $Script:EventSourceReady) { return $Script:EventSourceReady }
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists($Script:EventLogSource)) {
            [System.Diagnostics.EventLog]::CreateEventSource($Script:EventLogSource, $Script:EventLogName)
        }
        $Script:EventSourceReady = $true
    } catch {
        # Requires administrator rights to create the source; log to file/console only.
        $Script:EventSourceReady = $false
    }
    return $Script:EventSourceReady
}

function Write-Log {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('INFO','WARN','ERROR','OK','STEP')][string]$Level = 'INFO'
    )
    $ts   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $line = "[$ts] [$Level] $Message"
    try {
        $logFile = Join-Path $LogsDir ("activity-{0}.log" -f (Get-Date -Format 'yyyyMM'))
        Add-Content -LiteralPath $logFile -Value $line -ErrorAction SilentlyContinue
    } catch { }

    # Mirror to the Windows Event Log so activity is visible in Event Viewer.
    try {
        if (Initialize-EventLogSource) {
            $entryType = switch ($Level) { 'ERROR' { 'Error' } 'WARN' { 'Warning' } default { 'Information' } }
            $eventId   = switch ($Level) { 'ERROR' { 1001 }    'WARN' { 1002 }     default { 1000 } }
            Write-EventLog -LogName $Script:EventLogName -Source $Script:EventLogSource `
                -EntryType $entryType -EventId $eventId -Message $Message -ErrorAction SilentlyContinue
        }
    } catch { }

    if ($Unattended) { Write-Output $line; return }

    $color = switch ($Level) {
        'OK'    { 'Green' }
        'WARN'  { 'Yellow' }
        'ERROR' { 'Red' }
        'STEP'  { 'Cyan' }
        default { 'Gray' }
    }
    Write-Host $line -ForegroundColor $color
}

function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p  = New-Object Security.Principal.WindowsPrincipal($id)
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function Assert-Admin {
    param([string]$For = 'this operation')
    if (-not (Test-IsAdmin)) {
        throw "Administrator rights are required for $For. Re-launch PowerShell 'as Administrator'."
    }
}

# Re-launches the script elevated (UAC) when it isn't already running as admin,
# passing the original parameters through. Returns $true if it relaunched (the
# current, non-elevated instance should then exit), $false if already elevated.
function Invoke-SelfElevation {
    if (Test-IsAdmin) { return $false }

    if ($Unattended) {
        # A non-interactive context (e.g. the scheduled task) must already be
        # elevated; spawning an interactive UAC prompt here would never succeed.
        throw "Administrator rights are required but this is running unattended. " +
              "Configure the renewal task to run as SYSTEM with highest privileges."
    }

    Write-Host "  Administrator rights are required - requesting elevation (UAC)..." -ForegroundColor Yellow

    # Relaunch with the same host (powershell.exe or pwsh.exe) and the same args.
    try { $hostExe = (Get-Process -Id $PID).Path } catch { $hostExe = $null }
    if (-not $hostExe) { $hostExe = (Get-Command powershell.exe).Source }

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $Script:ScriptPath))
    foreach ($k in $Script:BoundParams.Keys) {
        $v = $Script:BoundParams[$k]
        if ($v -is [System.Management.Automation.SwitchParameter]) {
            if ($v.IsPresent) { $argList += ('-{0}' -f $k) }
        } elseif ($v -is [array]) {
            $argList += ('-{0}' -f $k); $argList += ('"{0}"' -f ($v -join ','))
        } else {
            $argList += ('-{0}' -f $k); $argList += ('"{0}"' -f $v)
        }
    }

    try {
        Start-Process -FilePath $hostExe -Verb RunAs -ArgumentList $argList | Out-Null
    } catch {
        throw "Elevation was cancelled or failed. This tool needs Administrator rights " +
              "to write the certificate store, configure IIS, and manage the renewal task."
    }
    return $true
}

# --- Atomic JSON helpers ---------------------------------------------------

function Read-Json {
    param([Parameter(Mandatory)][string]$Path, $Default)
    if (-not (Test-Path -LiteralPath $Path)) { return $Default }
    try {
        $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($raw)) { return $Default }
        return ($raw | ConvertFrom-Json -ErrorAction Stop)
    } catch {
        Write-Log "Could not parse $Path ($($_.Exception.Message)); using defaults." 'WARN'
        return $Default
    }
}

function Write-Json {
    param([Parameter(Mandatory)][string]$Path, $Value, [switch]$AsArray)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    # -InputObject (not pipeline) avoids PowerShell unwrapping single-element arrays.
    $json = ConvertTo-Json -InputObject $Value -Depth 12
    if ($AsArray -and -not $json.TrimStart().StartsWith('[')) { $json = "[$([Environment]::NewLine)$json$([Environment]::NewLine)]" }
    $tmp  = "$Path.tmp"
    Set-Content -LiteralPath $tmp -Value $json -Encoding UTF8
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

# StrictMode-safe optional property read for PSCustomObjects from JSON.
function Get-PropValue {
    param($Obj, [Parameter(Mandatory)][string]$Name, $Default = $null)
    if ($null -eq $Obj) { return $Default }
    $p = $Obj.PSObject.Properties[$Name]
    if ($p) { return $p.Value } else { return $Default }
}

# --- DPAPI secret protection (LocalMachine; matches the C# SecretProtector) -

function Protect-Secret {
    param([AllowNull()][string]$Plaintext)
    if ([string]::IsNullOrEmpty($Plaintext)) { return $Plaintext }
    Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
    $bytes = [Text.Encoding]::UTF8.GetBytes($Plaintext)
    $enc   = [System.Security.Cryptography.ProtectedData]::Protect(
                $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
    return 'DPAPI:' + [Convert]::ToBase64String($enc)
}

function Unprotect-Secret {
    param([AllowNull()][string]$Stored)
    if ([string]::IsNullOrEmpty($Stored)) { return $Stored }
    if (-not $Stored.StartsWith('DPAPI:')) { return $Stored }
    Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
    $enc   = [Convert]::FromBase64String($Stored.Substring(6))
    $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
                $enc, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [Text.Encoding]::UTF8.GetString($bytes)
}

function New-RandomPassword {
    param([int]$Length = 24)
    $bytes = New-Object byte[] $Length
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return [Convert]::ToBase64String($bytes).Substring(0, $Length)
}

#endregion

#region ----------------------------------------------------------- Settings store

function New-DefaultSettings {
    [pscustomobject]@{
        Environment      = $Script:Env_Staging
        ContactEmail     = ''
        Theme            = 0
        EnableAutoRenewal = $true
        Notifications    = [pscustomobject]@{
            NotifyOnSuccess       = $false
            NotifyOnFailure       = $true
            WebhookUrl            = $null
            EmailEnabled          = $false
            SmtpHost              = $null
            SmtpPort              = 587
            SmtpUseSsl            = $true
            SmtpUsername          = $null
            SmtpPasswordProtected = $null
            FromAddress           = $null
            ToAddress             = $null
        }
    }
}

function Get-Settings {
    $s = Read-Json -Path $SettingsFile -Default (New-DefaultSettings)
    # Backfill any missing members from defaults (forward-compatible).
    $def = New-DefaultSettings
    foreach ($p in $def.PSObject.Properties) {
        if (-not ($s.PSObject.Properties.Name -contains $p.Name)) {
            $s | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value -Force
        }
    }
    if (-not (Get-PropValue -Obj $s -Name 'Notifications')) {
        $s | Add-Member -NotePropertyName 'Notifications' -NotePropertyValue $def.Notifications -Force
    } else {
        foreach ($p in $def.Notifications.PSObject.Properties) {
            if (-not ($s.Notifications.PSObject.Properties.Name -contains $p.Name)) {
                $s.Notifications | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value -Force
            }
        }
    }
    return $s
}

function Save-Settings { param([Parameter(Mandatory)]$Settings) Write-Json -Path $SettingsFile -Value $Settings }

#endregion

#region ----------------------------------------------------------- Certificate store

function New-ManagedCertificate {
    [pscustomobject]@{
        Id                       = ([guid]::NewGuid().ToString('N'))
        Name                     = ''
        PrimaryDomain            = ''
        SubjectAlternativeNames  = @()
        ContactEmail             = ''
        ChallengeType            = $Script:Ch_Http01
        DnsProvider              = $Script:Dns_Manual
        DnsCredentialProtected   = $null
        DeploymentTasks          = @()
        IisSiteName              = $null
        FriendlyName             = $null
        RemoteTargets            = @()
        WebRootPath              = $null
        BindToIis                = $true
        AutoRenew                = $true
        RenewalDaysBeforeExpiry  = 30
        Thumbprint               = $null
        NotBefore                = $null
        NotAfter                 = $null
        LastRenewed              = $null
        LastError                = $null
        PfxPath                  = $null
    }
}

function Get-AllCertificates {
    $data = Read-Json -Path $CertsFile -Default @()
    if ($null -eq $data) { return @() }
    # ConvertFrom-Json returns a single object (not array) when there's one item.
    return @($data)
}

function Save-AllCertificates { param([Parameter(Mandatory)][AllowEmptyCollection()][array]$Certificates)
    $arr = @($Certificates)
    if ($arr.Count -eq 0) {
        Set-Content -LiteralPath $CertsFile -Value '[]' -Encoding UTF8
        return
    }
    Write-Json -Path $CertsFile -Value $arr -AsArray
}

function Set-Certificate {
    param([Parameter(Mandatory)]$Certificate)
    $all = @(Get-AllCertificates)
    $idx = -1
    for ($i = 0; $i -lt $all.Count; $i++) { if ($all[$i].Id -eq $Certificate.Id) { $idx = $i; break } }
    if ($idx -ge 0) { $all[$idx] = $Certificate } else { $all += $Certificate }
    Save-AllCertificates -Certificates $all
}

function Remove-Certificate {
    param([Parameter(Mandatory)][string]$CertId)
    $all = @(Get-AllCertificates | Where-Object { $_.Id -ne $CertId })
    Save-AllCertificates -Certificates $all
}

function Resolve-Certificate {
    # Accepts an exact Id, or a unique prefix of Id / Name / PrimaryDomain.
    param([Parameter(Mandatory)][string]$Selector)
    $all = @(Get-AllCertificates)
    $exact = $all | Where-Object { $_.Id -eq $Selector }
    if ($exact) { return $exact }
    $matches = @($all | Where-Object {
        $_.Id.StartsWith($Selector) -or
        ($_.Name -and $_.Name -like "*$Selector*") -or
        ($_.PrimaryDomain -and $_.PrimaryDomain -like "*$Selector*")
    })
    if ($matches.Count -eq 1) { return $matches[0] }
    if ($matches.Count -eq 0) { throw "No certificate matches '$Selector'." }
    throw "Selector '$Selector' is ambiguous ($($matches.Count) matches). Use a full Id."
}

function Get-AllDomains {
    param([Parameter(Mandatory)]$Cert)
    $list = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($Cert.PrimaryDomain)) { $list.Add($Cert.PrimaryDomain.Trim()) }
    foreach ($san in @($Cert.SubjectAlternativeNames)) {
        if ([string]::IsNullOrWhiteSpace($san)) { continue }
        $s = $san.Trim()
        if (-not ($list -contains $s)) { $list.Add($s) }
    }
    return $list.ToArray()
}

function Get-CertStatus {
    param([Parameter(Mandatory)]$Cert, [datetime]$Now = (Get-Date).ToUniversalTime())
    if ($Cert.LastError -and -not $Cert.NotAfter) { return $Script:St_Error }
    if (-not $Cert.NotAfter) { return $Script:St_NotRequested }
    $notAfter = [datetimeoffset]::Parse($Cert.NotAfter).UtcDateTime
    if ($Now -ge $notAfter) { return $Script:St_Expired }
    $days = if ($Cert.RenewalDaysBeforeExpiry) { [int]$Cert.RenewalDaysBeforeExpiry } else { 30 }
    if ($Now -ge $notAfter.AddDays(-$days)) { return $Script:St_ExpiringSoon }
    return $Script:St_Valid
}

function Get-StatusText {
    param([int]$Status)
    switch ($Status) {
        $Script:St_NotRequested { 'Not requested' }
        $Script:St_Valid        { 'Valid' }
        $Script:St_ExpiringSoon { 'Expiring soon' }
        $Script:St_Expired      { 'Expired' }
        $Script:St_Error        { 'Error' }
        default                 { 'Unknown' }
    }
}

function Get-StatusColor {
    param([int]$Status)
    switch ($Status) {
        $Script:St_Valid        { 'Green' }
        $Script:St_ExpiringSoon { 'Yellow' }
        $Script:St_Expired      { 'Red' }
        $Script:St_Error        { 'Red' }
        default                 { 'Gray' }
    }
}

function Test-IsDueForRenewal {
    param([Parameter(Mandatory)]$Cert, [datetime]$Now = (Get-Date).ToUniversalTime())
    if (-not $Cert.AutoRenew) { return $false }
    $s = Get-CertStatus -Cert $Cert -Now $Now
    return ($s -in @($Script:St_NotRequested, $Script:St_ExpiringSoon, $Script:St_Expired, $Script:St_Error))
}

#endregion

#region ----------------------------------------------------------- Posh-ACME integration

function Initialize-PoshAcme {
    # Share the ACME account/keys with the SYSTEM scheduled task by pinning HOME.
    $env:POSHACME_HOME = $PoshAcmeHome
    if (-not (Get-Module -ListAvailable -Name Posh-ACME)) {
        Write-Log "Posh-ACME module not found; installing (CurrentUser scope)..." 'STEP'
        try {
            if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
                Install-PackageProvider -Name NuGet -Force -Scope CurrentUser | Out-Null
            }
            Install-Module -Name Posh-ACME -Scope CurrentUser -Force -AllowClobber
        } catch {
            throw "Failed to install Posh-ACME automatically. Install it manually with: " +
                  "Install-Module Posh-ACME -Scope CurrentUser. ($($_.Exception.Message))"
        }
    }
    Import-Module Posh-ACME -ErrorAction Stop
}

function Set-AcmeContext {
    param([Parameter(Mandatory)][int]$EnvId, [string]$ContactEmail)
    $serverTag = if ($EnvId -eq $Script:Env_Production) { 'LE_PROD' } else { 'LE_STAGE' }
    Set-PAServer -DirectoryUrl $serverTag

    $acct = Get-PAAccount -ErrorAction SilentlyContinue
    if (-not $acct) {
        if ([string]::IsNullOrWhiteSpace($ContactEmail)) {
            throw "A contact email is required to register an ACME account. Set one in Settings."
        }
        Write-Log "Registering a new ACME account ($serverTag) for $ContactEmail..." 'STEP'
        New-PAAccount -AcceptTOS -Contact $ContactEmail -Force | Out-Null
    } elseif ($ContactEmail -and ($acct.contact -notcontains "mailto:$ContactEmail")) {
        try { Set-PAAccount -Contact $ContactEmail | Out-Null } catch { }
    }
}

#endregion

#region ----------------------------------------------------------- Cert store / IIS

function Import-CertToStore {
    # Imports a PFX into LocalMachine\My as exportable, removing older certs with
    # the same subject (mirrors the C# WindowsCertificateStore behaviour).
    param(
        [Parameter(Mandatory)][string]$PfxPath,
        [Parameter(Mandatory)][string]$Password,
        [string]$FriendlyName
    )
    $secure = ConvertTo-SecureString -String $Password -AsPlainText -Force
    $imported = @(Import-PfxCertificate -FilePath $PfxPath `
        -CertStoreLocation 'Cert:\LocalMachine\My' -Password $secure -Exportable)

    # A full-chain PFX imports the leaf plus chain certs; pick the leaf (has the key).
    $installed = $imported | Where-Object { $_.HasPrivateKey } | Select-Object -First 1
    if (-not $installed) { $installed = $imported | Select-Object -First 1 }

    # Set the friendly name on the store entry so IIS shows it with a recognisable
    # label in its Server Certificates list. Setting it via the Cert: provider
    # persists the change to the store.
    if (-not [string]::IsNullOrWhiteSpace($FriendlyName)) {
        try {
            $storeItem = Get-Item -LiteralPath ("Cert:\LocalMachine\My\{0}" -f $installed.Thumbprint) -ErrorAction Stop
            $storeItem.FriendlyName = $FriendlyName
        } catch { Write-Log "Could not set the certificate friendly name: $($_.Exception.Message)" 'WARN' }
    }

    # Remove older certs sharing the subject but with an earlier/equal expiry.
    try {
        Get-ChildItem 'Cert:\LocalMachine\My' |
            Where-Object {
                $_.Subject -eq $installed.Subject -and
                $_.Thumbprint -ne $installed.Thumbprint -and
                $_.NotAfter -le $installed.NotAfter
            } | ForEach-Object { Remove-Item -LiteralPath $_.PSPath -Force -ErrorAction SilentlyContinue }
    } catch { Write-Log "Could not prune older certificates: $($_.Exception.Message)" 'WARN' }

    return $installed
}

function Get-IisModule {
    if (Get-Module -ListAvailable -Name WebAdministration) {
        Import-Module WebAdministration -ErrorAction SilentlyContinue
        return $true
    }
    return $false
}

function Get-IisSites {
    if (-not (Get-IisModule)) { return @() }
    try {
        return @(Get-Website | ForEach-Object {
            [pscustomobject]@{
                Name         = $_.Name
                PhysicalPath = [Environment]::ExpandEnvironmentVariables($_.physicalPath)
                Bindings     = @($_.bindings.Collection | ForEach-Object { "$($_.protocol)://$($_.bindingInformation)" })
            }
        })
    } catch { return @() }
}

function Get-IisSitePhysicalPath {
    param([Parameter(Mandatory)][string]$SiteName)
    $site = Get-IisSites | Where-Object { $_.Name -eq $SiteName } | Select-Object -First 1
    if ($site) { return $site.PhysicalPath }
    return $null
}

function Set-IisBinding {
    # Adds/updates an HTTPS SNI binding for $HostName on $SiteName -> $Thumbprint.
    param(
        [Parameter(Mandatory)][string]$SiteName,
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][string]$Thumbprint,
        [int]$Port = 443
    )
    if (-not (Get-IisModule)) { throw "The WebAdministration module (IIS) is not available on this machine." }

    $existing = Get-WebBinding -Name $SiteName -Protocol 'https' -Port $Port -HostHeader $HostName -ErrorAction SilentlyContinue
    if ($existing) {
        try { Remove-WebBinding -Name $SiteName -Protocol 'https' -Port $Port -HostHeader $HostName -ErrorAction SilentlyContinue } catch { }
    }

    # SslFlags 1 = SNI.
    New-WebBinding -Name $SiteName -Protocol 'https' -Port $Port -HostHeader $HostName -SslFlags 1 -ErrorAction Stop | Out-Null
    $binding = Get-WebBinding -Name $SiteName -Protocol 'https' -Port $Port -HostHeader $HostName
    $binding.AddSslCertificate($Thumbprint, 'My')
}

function Invoke-IisBind {
    param([Parameter(Mandatory)]$Cert, [Parameter(Mandatory)][string]$SiteName)
    if ([string]::IsNullOrEmpty($Cert.Thumbprint)) {
        throw "This certificate hasn't been issued yet, so there's nothing to bind."
    }
    foreach ($domain in (Get-AllDomains -Cert $Cert)) {
        if ($domain.StartsWith('*.')) { continue }   # wildcard hosts aren't valid SNI host names
        Write-Log "Binding $domain -> IIS site '$SiteName'." 'STEP'
        Set-IisBinding -SiteName $SiteName -HostName $domain -Thumbprint $Cert.Thumbprint
    }
}

function New-RemoteIisTarget {
    # A remote Windows/IIS server the certificate is distributed to on renewal.
    param(
        [Parameter(Mandatory)][string]$HostName,
        [int]$WinRmPort = 5986,
        [bool]$UseSsl = $true,
        [string[]]$SiteNames = @()
    )
    [pscustomobject]@{
        Host         = $HostName
        WinRmPort    = $WinRmPort
        UseSsl       = $UseSsl
        SiteNames    = @($SiteNames)
        LastDeployed = $null
        LastError    = $null
    }
}

function ConvertTo-RemoteIisTarget {
    # Parses a CLI spec like "host=web2;sites=Default Web Site,api;port=5985;ssl=0".
    param([Parameter(Mandatory)][string]$Spec)
    $map = @{}
    foreach ($pair in ($Spec -split ';')) {
        $idx = $pair.IndexOf('=')
        if ($idx -gt 0) {
            $key = $pair.Substring(0, $idx).Trim().ToLowerInvariant()
            $map[$key] = $pair.Substring($idx + 1).Trim()
        }
    }
    if (-not $map.ContainsKey('host') -or [string]::IsNullOrWhiteSpace($map['host'])) {
        throw "A -RemoteTarget spec must include host= (got '$Spec')."
    }
    $sites = @()
    if ($map.ContainsKey('sites')) { $sites = @($map['sites'] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    $port  = if ($map.ContainsKey('port')) { [int]$map['port'] } else { 5986 }
    $useSsl = if ($map.ContainsKey('ssl')) { $map['ssl'] -in @('1','true','yes') } else { $true }
    New-RemoteIisTarget -HostName $map['host'] -WinRmPort $port -UseSsl $useSsl -SiteNames $sites
}

function Invoke-RemoteIisDeploy {
    # Distributes an issued certificate to one remote server over WinRM /
    # PowerShell Remoting, authenticating as the current identity (a domain
    # service account -> Kerberos; no stored credentials). Imports the PFX into
    # the remote LocalMachine\My with the friendly name and binds the given IIS
    # sites with SNI, mirroring the local install/bind.
    param(
        [Parameter(Mandatory)]$Cert,
        [Parameter(Mandatory)]$Target,
        [Parameter(Mandatory)][string]$PfxPath,
        [Parameter(Mandatory)][string]$PfxPassword
    )
    $targetHost = $Target.Host
    if ([string]::IsNullOrWhiteSpace($targetHost)) { throw "A remote target has no host name." }

    $pfxB64   = [Convert]::ToBase64String([IO.File]::ReadAllBytes($PfxPath))
    $friendly = Get-PropValue -Obj $Cert -Name 'FriendlyName'
    $domains  = @(Get-AllDomains -Cert $Cert | Where-Object { -not $_.StartsWith('*.') })
    $sites    = @($Target.SiteNames)

    $remote = {
        param($PfxB64, $PfxPass, $FriendlyName, $Domains, $Sites)
        $ErrorActionPreference = 'Stop'
        $bytes = [Convert]::FromBase64String($PfxB64)
        $tmp = Join-Path $env:TEMP ([guid]::NewGuid().ToString('N') + '.pfx')
        [IO.File]::WriteAllBytes($tmp, $bytes)
        try {
            $secure = ConvertTo-SecureString -String $PfxPass -AsPlainText -Force
            $imported = @(Import-PfxCertificate -FilePath $tmp -CertStoreLocation 'Cert:\LocalMachine\My' -Password $secure -Exportable)
            $installed = $imported | Where-Object { $_.HasPrivateKey } | Select-Object -First 1
            if (-not $installed) { $installed = $imported | Select-Object -First 1 }

            if (-not [string]::IsNullOrWhiteSpace($FriendlyName)) {
                $item = Get-Item -LiteralPath ("Cert:\LocalMachine\My\{0}" -f $installed.Thumbprint)
                $item.FriendlyName = $FriendlyName
            }

            Get-ChildItem 'Cert:\LocalMachine\My' | Where-Object {
                $_.Subject -eq $installed.Subject -and
                $_.Thumbprint -ne $installed.Thumbprint -and
                $_.NotAfter -le $installed.NotAfter
            } | ForEach-Object { Remove-Item -LiteralPath $_.PSPath -Force -ErrorAction SilentlyContinue }

            if (@($Sites).Count -gt 0) {
                Import-Module WebAdministration -ErrorAction Stop
                foreach ($site in $Sites) {
                    foreach ($d in $Domains) {
                        $existing = Get-WebBinding -Name $site -Protocol 'https' -Port 443 -HostHeader $d -ErrorAction SilentlyContinue
                        if ($existing) { Remove-WebBinding -Name $site -Protocol 'https' -Port 443 -HostHeader $d -ErrorAction SilentlyContinue }
                        New-WebBinding -Name $site -Protocol 'https' -Port 443 -HostHeader $d -SslFlags 1 -ErrorAction Stop | Out-Null
                        $b = Get-WebBinding -Name $site -Protocol 'https' -Port 443 -HostHeader $d
                        $b.AddSslCertificate($installed.Thumbprint, 'My')
                    }
                }
            }
        } finally {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
    }

    $icmArgs = @{
        ComputerName = $targetHost
        Port         = [int]$Target.WinRmPort
        ScriptBlock  = $remote
        ArgumentList = @($pfxB64, $PfxPassword, $friendly, $domains, $sites)
        ErrorAction  = 'Stop'
    }
    if ($Target.UseSsl) { $icmArgs['UseSSL'] = $true }

    Write-Log "Deploying $($Cert.PrimaryDomain) -> remote server $targetHost (sites: $($sites -join ', '))." 'STEP'
    Invoke-Command @icmArgs | Out-Null
}

#endregion

#region ----------------------------------------------------------- Deployment tasks

function Invoke-DeploymentTasks {
    param(
        [Parameter(Mandatory)]$Cert,
        [Parameter(Mandatory)][string]$PfxPath,
        [Parameter(Mandatory)][string]$PfxPassword,
        [Parameter(Mandatory)]$Installed
    )
    foreach ($task in @($Cert.DeploymentTasks)) {
        try {
            switch ([int]$task.Type) {
                $Script:Dep_ExportPfx {
                    $path = Get-PropValue -Obj $task.Settings -Name 'Path'
                    if (-not $path) { throw "Export PFX task requires a 'Path' setting." }
                    $dir = Split-Path -Parent ([IO.Path]::GetFullPath($path))
                    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
                    $pw = Get-PropValue -Obj $task.Settings -Name 'Password'
                    if ([string]::IsNullOrEmpty($pw)) {
                        Copy-Item -LiteralPath $PfxPath -Destination $path -Force
                    } else {
                        $bytes = $Installed.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $pw)
                        [IO.File]::WriteAllBytes($path, $bytes)
                    }
                    Write-Log "Deployment: exported PFX to $path." 'OK'
                }
                $Script:Dep_ExportPem {
                    $dir = Get-PropValue -Obj $task.Settings -Name 'Path'
                    if (-not $dir) { throw "Export PEM task requires a 'Path' directory setting." }
                    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
                    Export-PemFiles -PfxPath $PfxPath -Password $PfxPassword -Directory $dir
                    Write-Log "Deployment: exported fullchain.pem + privkey.pem to $dir." 'OK'
                }
                $Script:Dep_RunScript {
                    $path = Get-PropValue -Obj $task.Settings -Name 'Path'
                    if (-not $path) { throw "Run script task requires a 'Path' setting." }
                    $arguments = Get-PropValue -Obj $task.Settings -Name 'Arguments' -Default ''
                    Invoke-PostIssueScript -ScriptPath $path -Arguments $arguments -Cert $Cert -PfxPassword $PfxPassword
                    Write-Log "Deployment: ran script $([IO.Path]::GetFileName($path))." 'OK'
                }
                default { Write-Log "Unknown deployment task type: $($task.Type)" 'WARN' }
            }
        } catch {
            Write-Log "Deployment task failed: $($_.Exception.Message)" 'ERROR'
        }
    }
}

function Export-PemFiles {
    param(
        [Parameter(Mandatory)][string]$PfxPath,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$Directory
    )
    $secure = ConvertTo-SecureString -String $Password -AsPlainText -Force
    $collection = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable -bor `
             [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    $collection.Import($PfxPath, $Password, $flags)

    $leaf = $collection | Where-Object { $_.HasPrivateKey } | Select-Object -First 1
    if (-not $leaf) { $leaf = $collection[0] }

    $fullchain = ($collection | ForEach-Object { $_.ExportCertificatePem() }) -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $Directory 'fullchain.pem') -Value $fullchain -Encoding Ascii

    $keyPem = $null
    $rsa = $leaf.GetRSAPrivateKey()
    if ($rsa) { $keyPem = $rsa.ExportPkcs8PrivateKeyPem() }
    else {
        $ec = $leaf.GetECDsaPrivateKey()
        if ($ec) { $keyPem = $ec.ExportPkcs8PrivateKeyPem() }
    }
    if (-not $keyPem) { throw "The certificate has no exportable private key." }
    Set-Content -LiteralPath (Join-Path $Directory 'privkey.pem') -Value $keyPem -Encoding Ascii
}

function Invoke-PostIssueScript {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [string]$Arguments,
        [Parameter(Mandatory)]$Cert,
        [Parameter(Mandatory)][string]$PfxPassword
    )
    $env:LETSSSL4WINDOWS_THUMBPRINT   = $Cert.Thumbprint
    $env:LETSSSL4WINDOWS_DOMAIN       = $Cert.PrimaryDomain
    $env:LETSSSL4WINDOWS_PFX_PATH     = $Cert.PfxPath
    $env:LETSSSL4WINDOWS_PFX_PASSWORD = $PfxPassword
    try {
        if ($ScriptPath.ToLower().EndsWith('.ps1')) {
            $psArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`" $Arguments"
            $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $psArgs -NoNewWindow -Wait -PassThru
        } else {
            $p = Start-Process -FilePath $ScriptPath -ArgumentList $Arguments -NoNewWindow -Wait -PassThru
        }
        if ($p.ExitCode -ne 0) { throw "Script exited with code $($p.ExitCode)." }
    } finally {
        Remove-Item Env:LETSSSL4WINDOWS_PFX_PASSWORD -ErrorAction SilentlyContinue
    }
}

#endregion

#region ----------------------------------------------------------- Notifications

function Send-IssuanceNotification {
    param(
        [Parameter(Mandatory)]$Cert,
        [Parameter(Mandatory)][bool]$Success,
        [string]$ErrorMessage,
        [string[]]$Warnings   # non-fatal problems (e.g. failed remote deployments)
    )
    $settings = (Get-Settings).Notifications
    $hasWarnings = @($Warnings).Count -gt 0
    # A success carrying warnings is a partial failure: gate it on NotifyOnFailure.
    if ($Success -and -not $hasWarnings -and -not $settings.NotifyOnSuccess) { return }
    if ($Success -and $hasWarnings -and -not $settings.NotifyOnFailure) { return }
    if (-not $Success -and -not $settings.NotifyOnFailure) { return }

    $domains = (Get-AllDomains -Cert $Cert) -join ', '
    if ($Success -and $hasWarnings) {
        $detail  = ($Warnings | ForEach-Object { "  - $_" }) -join "`n"
        $subject = "Certificate issued for $($Cert.PrimaryDomain), but $(@($Warnings).Count) remote deployment(s) FAILED"
        $body    = "A certificate for $domains was issued/renewed and installed locally, but the " +
                   "following remote deployment(s) failed:`n$detail`n`nValid until: $($Cert.NotAfter)`nThumbprint: $($Cert.Thumbprint)"
    } elseif ($Success) {
        $subject = "Certificate issued for $($Cert.PrimaryDomain)"
        $body    = "A certificate for $domains was issued/renewed successfully.`n" +
                   "Valid until: $($Cert.NotAfter)`nThumbprint: $($Cert.Thumbprint)"
    } else {
        $subject = "Certificate renewal FAILED for $($Cert.PrimaryDomain)"
        $body    = "Issuance/renewal for $domains failed.`nError: $(if ($ErrorMessage) { $ErrorMessage } else { 'unknown error' })"
    }
    $statusText = if ($Success -and -not $hasWarnings) { 'success' } else { 'failure' }

    # Webhook
    if (-not [string]::IsNullOrWhiteSpace($settings.WebhookUrl)) {
        try {
            $payload = @{
                text      = "[$AppName] $subject"
                status    = $statusText
                domain    = $Cert.PrimaryDomain
                subject   = $subject
                body      = $body
                timestamp = (Get-Date).ToUniversalTime().ToString('o')
            } | ConvertTo-Json -Depth 4
            Invoke-RestMethod -Uri $settings.WebhookUrl -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 30 | Out-Null
            Write-Log "Sent webhook notification for $($Cert.PrimaryDomain)." 'OK'
        } catch { Write-Log "Failed to send webhook notification: $($_.Exception.Message)" 'WARN' }
    }

    # Email (SMTP)
    if ($settings.EmailEnabled -and $settings.SmtpHost -and $settings.FromAddress -and $settings.ToAddress) {
        try {
            $mailParams = @{
                SmtpServer = $settings.SmtpHost
                Port       = [int]$settings.SmtpPort
                UseSsl     = [bool]$settings.SmtpUseSsl
                From       = $settings.FromAddress
                To         = $settings.ToAddress
                Subject    = "[$AppName] $subject"
                Body       = $body
            }
            if ($settings.SmtpUsername) {
                $pw  = Unprotect-Secret -Stored $settings.SmtpPasswordProtected
                $sec = ConvertTo-SecureString -String $pw -AsPlainText -Force
                $mailParams.Credential = New-Object System.Management.Automation.PSCredential($settings.SmtpUsername, $sec)
            }
            Send-MailMessage @mailParams -ErrorAction Stop
            Write-Log "Sent email notification for $($Cert.PrimaryDomain)." 'OK'
        } catch { Write-Log "Failed to send email notification: $($_.Exception.Message)" 'WARN' }
    }
}

#endregion

#region ----------------------------------------------------------- Core: request & deploy

function Invoke-RequestAndDeploy {
    param([Parameter(Mandatory)]$Cert, [Parameter(Mandatory)][int]$EnvId)

    Initialize-Paths
    try {
        $names = Get-AllDomains -Cert $Cert
        if ($names.Count -eq 0) { throw "No domains configured for this certificate." }

        $isWildcard = @($names | Where-Object { $_.StartsWith('*.') }).Count -gt 0
        if ($isWildcard -and [int]$Cert.ChallengeType -ne $Script:Ch_Dns01) {
            throw "Wildcard domains require DNS-01 validation."
        }

        Initialize-PoshAcme
        $contact = if ($Cert.ContactEmail) { $Cert.ContactEmail } else { (Get-Settings).ContactEmail }
        Set-AcmeContext -EnvId $EnvId -ContactEmail $contact

        # --- Build the challenge plugin + args ---
        if ([int]$Cert.ChallengeType -eq $Script:Ch_Dns01) {
            if ([int]$Cert.DnsProvider -eq $Script:Dns_Cloudflare) {
                $token = Unprotect-Secret -Stored $Cert.DnsCredentialProtected
                if ([string]::IsNullOrWhiteSpace($token)) { throw "A Cloudflare API token is required." }
                $plugin  = 'Cloudflare'
                $pArgs   = @{ CFToken = (ConvertTo-SecureString -String $token -AsPlainText -Force) }
            } else {
                if ($Unattended) {
                    throw "Manual DNS validation is interactive-only and cannot run unattended. Use Cloudflare for unattended renewal."
                }
                $plugin = 'Manual'
                $pArgs  = @{}
            }
        } else {
            $webRootBase = $Cert.WebRootPath
            if ([string]::IsNullOrWhiteSpace($webRootBase) -and $Cert.IisSiteName) {
                $webRootBase = Get-IisSitePhysicalPath -SiteName $Cert.IisSiteName
            }
            if ([string]::IsNullOrWhiteSpace($webRootBase)) {
                throw "Could not determine a web root for HTTP-01. Set a web root path or choose an IIS site."
            }
            $challengeDir = Join-Path $webRootBase '.well-known\acme-challenge'
            if (-not (Test-Path -LiteralPath $challengeDir)) {
                New-Item -ItemType Directory -Path $challengeDir -Force | Out-Null
            }
            $plugin = 'WebRoot'
            $pArgs  = @{ WRPath = @($challengeDir); WRExactPath = $true }
        }

        Write-Log "Requesting certificate for: $($names -join ', ')" 'STEP'
        $pfxPass = New-RandomPassword
        # NB: use a distinct variable name - $cert would clobber the $Cert parameter
        # (PowerShell variable names are case-insensitive).
        $paCert = New-PACertificate -Domain $names -Plugin $plugin -PluginArgs $pArgs `
                    -PfxPass $pfxPass -Force -ErrorAction Stop

        $sourcePfx = if (Get-PropValue -Obj $paCert -Name 'PfxFullChain') { $paCert.PfxFullChain } else { $paCert.PfxFile }
        if (-not $sourcePfx -or -not (Test-Path $sourcePfx)) { throw "Posh-ACME did not return a PFX file." }

        Write-Log "Installing certificate into LocalMachine\My..." 'STEP'
        $friendlyName = Get-PropValue -Obj $Cert -Name 'FriendlyName'
        $installed = Import-CertToStore -PfxPath $sourcePfx -Password $pfxPass -FriendlyName $friendlyName

        # Persist a copy of the PFX in our store.
        $destPfx = Join-Path $PfxDir ("{0}.pfx" -f $Cert.Id)
        Copy-Item -LiteralPath $sourcePfx -Destination $destPfx -Force

        $Cert.Thumbprint  = $installed.Thumbprint
        $Cert.NotBefore   = ([datetimeoffset]$installed.NotBefore).ToUniversalTime().ToString('o')
        $Cert.NotAfter    = ([datetimeoffset]$installed.NotAfter).ToUniversalTime().ToString('o')
        $Cert.LastRenewed = (Get-Date).ToUniversalTime().ToString('o')
        $Cert.PfxPath     = $destPfx
        $Cert.LastError   = $null

        if ($Cert.BindToIis -and $Cert.IisSiteName) {
            Invoke-IisBind -Cert $Cert -SiteName $Cert.IisSiteName
        }

        # Distribute to remote IIS servers (single source of truth: this instance
        # renews and pushes to each target). One failure never blocks the others.
        $remoteWarnings = @()
        foreach ($rt in @(Get-PropValue -Obj $Cert -Name 'RemoteTargets')) {
            if (-not $rt) { continue }
            try {
                Invoke-RemoteIisDeploy -Cert $Cert -Target $rt -PfxPath $destPfx -PfxPassword $pfxPass
                $rt.LastDeployed = (Get-Date).ToUniversalTime().ToString('o')
                $rt.LastError = $null
            } catch {
                $rt.LastError = $_.Exception.Message
                $remoteWarnings += "$($rt.Host): $($rt.LastError)"
                Write-Log "Remote deployment to $($rt.Host) failed: $($rt.LastError)" 'ERROR'
            }
        }

        if (@($Cert.DeploymentTasks).Count -gt 0) {
            Write-Log "Running deployment tasks..." 'STEP'
            Invoke-DeploymentTasks -Cert $Cert -PfxPath $destPfx -PfxPassword $pfxPass -Installed $installed
        }

        Set-Certificate -Certificate $Cert
        Write-Log "Certificate for $($Cert.PrimaryDomain) is ready (expires $($Cert.NotAfter))." 'OK'
        Send-IssuanceNotification -Cert $Cert -Success $true -Warnings $remoteWarnings
        return $Cert
    } catch {
        $msg = $_.Exception.Message
        Write-Log "Issuance failed for $($Cert.PrimaryDomain): $msg" 'ERROR'
        $Cert.LastError = $msg
        Set-Certificate -Certificate $Cert
        try { Send-IssuanceNotification -Cert $Cert -Success $false -ErrorMessage $msg } catch { }
        throw
    }
}

#endregion

#region ----------------------------------------------------------- Renewal

function Invoke-RenewDue {
    Initialize-Paths
    $settings = Get-Settings
    if (-not $settings.EnableAutoRenewal) {
        Write-Log "Automatic renewal is disabled in Settings; nothing to do." 'WARN'
        return
    }
    $now  = (Get-Date).ToUniversalTime()
    $due  = @(Get-AllCertificates | Where-Object { Test-IsDueForRenewal -Cert $_ -Now $now })
    $succeeded = 0; $failed = 0

    if ($due.Count -eq 0) {
        Write-Log "No certificates are due for renewal." 'OK'
    } else {
        Write-Log "$($due.Count) certificate(s) due for renewal." 'STEP'
        foreach ($c in $due) {
            try {
                Invoke-RequestAndDeploy -Cert $c -EnvId ([int]$settings.Environment) | Out-Null
                $succeeded++
            } catch {
                $failed++
                Write-Log "Renewal failed for $($c.PrimaryDomain): $($_.Exception.Message)" 'ERROR'
            }
        }
    }

    Write-Json -Path $LastRunFile -Value ([pscustomobject]@{
        LastRunUtc = $now.ToString('o'); Succeeded = $succeeded; Failed = $failed
    })
    Write-Log "Renewal run complete. Succeeded: $succeeded, Failed: $failed." 'OK'
}

#endregion

#region ----------------------------------------------------------- Scheduled task

function Install-RenewalTask {
    Assert-Admin -For 'registering the renewal scheduled task'
    Initialize-Paths

    $psExe = (Get-Command powershell.exe).Source
    $arg   = "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`" -Command RenewDue -Unattended"
    $action  = New-ScheduledTaskAction -Execute $psExe -Argument $arg
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddHours(3) `
                 -RepetitionInterval (New-TimeSpan -Hours 12) `
                 -RepetitionDuration (New-TimeSpan -Days 3650)
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopOnIdleEnd `
                   -ExecutionTimeLimit (New-TimeSpan -Hours 2)

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
    Write-Log "Registered scheduled task '$TaskName' (runs every 12 hours as SYSTEM)." 'OK'
}

function Uninstall-RenewalTask {
    Assert-Admin -For 'removing the renewal scheduled task'
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Log "Removed scheduled task '$TaskName'." 'OK'
    } else {
        Write-Log "Scheduled task '$TaskName' is not installed." 'WARN'
    }
}

function Get-RenewalTaskState {
    $t = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($t) { return "Installed ($($t.State))" }
    return 'Not installed'
}

#endregion

#region ----------------------------------------------------------- On-demand export

function Export-IssuedCertificate {
    param(
        [Parameter(Mandatory)]$Cert,
        [ValidateSet('Pfx','Pem')][string]$Type = 'Pfx',
        [Parameter(Mandatory)][string]$Destination,
        [string]$Password
    )
    # Export reads from the LocalMachine store (certs are imported as exportable),
    # so a saved PFX copy isn't required - this also works for imported records.
    if ([string]::IsNullOrEmpty($Cert.Thumbprint)) { throw "This certificate hasn't been issued yet, so there's nothing to export." }
    $installed = Get-ChildItem 'Cert:\LocalMachine\My' | Where-Object { $_.Thumbprint -eq $Cert.Thumbprint } | Select-Object -First 1
    if (-not $installed) { throw "Certificate $($Cert.Thumbprint) not found in LocalMachine\My." }

    if ($Type -eq 'Pfx') {
        $pw = if ($Password) { $Password } else { '' }
        $bytes = $installed.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $pw)
        [IO.File]::WriteAllBytes($Destination, $bytes)
        Write-Log "Exported PFX to $Destination." 'OK'
    } else {
        if (-not (Test-Path $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }
        Set-Content -LiteralPath (Join-Path $Destination 'fullchain.pem') -Value $installed.ExportCertificatePem() -Encoding Ascii
        $keyPem = $null
        $rsa = $installed.GetRSAPrivateKey()
        if ($rsa) { $keyPem = $rsa.ExportPkcs8PrivateKeyPem() }
        else { $ec = $installed.GetECDsaPrivateKey(); if ($ec) { $keyPem = $ec.ExportPkcs8PrivateKeyPem() } }
        if (-not $keyPem) { throw "The certificate has no exportable private key." }
        Set-Content -LiteralPath (Join-Path $Destination 'privkey.pem') -Value $keyPem -Encoding Ascii
        Write-Log "Exported fullchain.pem + privkey.pem to $Destination." 'OK'
    }
}

#endregion

#region ----------------------------------------------------------- Import / rescan

# Extracts the DNS names from a certificate (CN first, then SAN entries).
# Locale-tolerant: parses the value after '=' on each SAN line rather than
# relying on the localized "DNS Name" label.
function Get-CertDnsNames {
    param([Parameter(Mandatory)]$Cert)
    $names = New-Object System.Collections.Generic.List[string]
    try {
        $cn = $Cert.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::DnsName, $false)
        if ($cn) { $names.Add($cn) }
    } catch { }

    $ext = $Cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' } | Select-Object -First 1
    if ($ext) {
        $txt = $ext.Format($true)
        foreach ($line in ($txt -split "`r`n|`r|`n")) {
            if ($line -match '=\s*([^\s,]+)\s*$') {
                $n = $matches[1].Trim()
                if ($n -and ($names -notcontains $n)) { $names.Add($n) }
            }
        }
    }
    return $names
}

# Scans LocalMachine\My for issued certificates that aren't yet tracked in
# certificates.json and adds a managed record for each (matched by thumbprint).
# By default only Let's Encrypt-issued certificates are imported.
function Import-ExistingCertificates {
    param([switch]$IncludeNonLetsEncrypt)
    Initialize-Paths

    $tracked = @{}
    foreach ($c in @(Get-AllCertificates)) {
        if ($c.Thumbprint) { $tracked[$c.Thumbprint.ToUpperInvariant()] = $true }
    }

    try {
        $storeCerts = @(Get-ChildItem 'Cert:\LocalMachine\My' -ErrorAction Stop | Where-Object { $_.HasPrivateKey })
    } catch {
        throw "Could not read Cert:\LocalMachine\My (run elevated). $($_.Exception.Message)"
    }

    $settings = Get-Settings
    $candidates = @{}   # keyed by sorted domain set, newest cert wins

    foreach ($sc in $storeCerts) {
        if ($tracked.ContainsKey($sc.Thumbprint.ToUpperInvariant())) { continue }
        $isLE = $sc.Issuer -match "Let's Encrypt"
        if (-not $isLE -and -not $IncludeNonLetsEncrypt) { continue }

        $names = @(Get-CertDnsNames -Cert $sc)
        if ($names.Count -eq 0) { continue }
        $primary = $names[0]
        $key = (@($names | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object)) -join '|'

        $prior = $candidates[$key]
        if ($prior -and ([datetime]$prior._notAfter -ge $sc.NotAfter)) { continue }

        $isWildcard = (@($names | Where-Object { $_.StartsWith('*.') }).Count) -gt 0
        $rec = New-ManagedCertificate
        $rec.Name                    = $primary
        $rec.PrimaryDomain           = $primary
        $rec.SubjectAlternativeNames = @($names | Select-Object -Skip 1)
        $rec.ContactEmail            = $settings.ContactEmail
        $rec.ChallengeType           = if ($isWildcard) { $Script:Ch_Dns01 } else { $Script:Ch_Http01 }
        $rec.DnsProvider             = $Script:Dns_Manual
        $rec.Thumbprint              = $sc.Thumbprint
        $rec.NotBefore               = ([datetimeoffset]$sc.NotBefore).ToUniversalTime().ToString('o')
        $rec.NotAfter                = ([datetimeoffset]$sc.NotAfter).ToUniversalTime().ToString('o')
        # Request arguments (DNS creds, IIS site, deployment tasks) aren't
        # recoverable from the cert, so leave auto-renew/binding off for review.
        $rec.AutoRenew               = $false
        $rec.BindToIis               = $false

        $candidates[$key] = ($rec | Add-Member -NotePropertyName _notAfter -NotePropertyValue $sc.NotAfter -PassThru -Force)
    }

    $imported = @($candidates.Values)
    if ($imported.Count -eq 0) {
        Write-Log "No untracked certificates found in LocalMachine\My." 'OK'
        return @()
    }

    foreach ($rec in $imported) {
        $rec.PSObject.Properties.Remove('_notAfter')
        Set-Certificate -Certificate $rec
        Write-Log "Imported $($rec.PrimaryDomain)  (thumbprint $($rec.Thumbprint))." 'OK'
    }
    Write-Log "Imported $($imported.Count) certificate(s). Review them and enable auto-renew / IIS binding as needed." 'OK'
    return $imported
}

#endregion

#region ----------------------------------------------------------- Console UI helpers

function Write-Banner {
    Write-Host ''
    Write-Host '  ====================================================' -ForegroundColor DarkCyan
    Write-Host '   LetsSSL4Windows  -  PowerShell edition' -ForegroundColor Cyan
    Write-Host '   Free Let''s Encrypt certificate manager for Windows' -ForegroundColor DarkGray
    Write-Host '  ====================================================' -ForegroundColor DarkCyan
}

function Read-Default {
    param([string]$Prompt, [string]$Default)
    $suffix = if ($Default) { " [$Default]" } else { '' }
    $val = Read-Host ("{0}{1}" -f $Prompt, $suffix)
    if ([string]::IsNullOrWhiteSpace($val)) { return $Default }
    return $val
}

function Read-YesNo {
    param([string]$Prompt, [bool]$Default = $true)
    $hint = if ($Default) { 'Y/n' } else { 'y/N' }
    $val = Read-Host ("{0} [{1}]" -f $Prompt, $hint)
    if ([string]::IsNullOrWhiteSpace($val)) { return $Default }
    return ($val -match '^(y|yes)$')
}

function Show-CertificateTable {
    $all = @(Get-AllCertificates)
    if ($all.Count -eq 0) { Write-Host "`n  No managed certificates yet. Use 'New certificate' to add one.`n" -ForegroundColor DarkGray; return }
    Write-Host ''
    Write-Host ('  {0,-10} {1,-28} {2,-14} {3,-20}' -f 'Id', 'Primary domain', 'Status', 'Expires') -ForegroundColor White
    Write-Host ('  ' + ('-' * 74)) -ForegroundColor DarkGray
    foreach ($c in $all) {
        $st     = Get-CertStatus -Cert $c
        $stTxt  = Get-StatusText -Status $st
        $color  = Get-StatusColor -Status $st
        $exp    = if ($c.NotAfter) { ([datetimeoffset]::Parse($c.NotAfter)).LocalDateTime.ToString('yyyy-MM-dd') } else { '-' }
        $shortId = $c.Id.Substring(0, [Math]::Min(8, $c.Id.Length))
        Write-Host ('  {0,-10} {1,-28} ' -f $shortId, $c.PrimaryDomain) -NoNewline
        Write-Host ('{0,-14}' -f $stTxt) -ForegroundColor $color -NoNewline
        Write-Host (' {0,-20}' -f $exp)
    }
    Write-Host ''
}

function Show-CertificateDetail {
    param([Parameter(Mandatory)]$Cert)
    $st = Get-StatusText -Status (Get-CertStatus -Cert $Cert)
    Write-Host ''
    Write-Host "  Certificate: $($Cert.Name)" -ForegroundColor Cyan
    Write-Host "    Id              : $($Cert.Id)"
    Write-Host "    Primary domain  : $($Cert.PrimaryDomain)"
    Write-Host "    SANs            : $((@($Cert.SubjectAlternativeNames)) -join ', ')"
    Write-Host "    Challenge       : $(if ([int]$Cert.ChallengeType -eq $Script:Ch_Dns01) { 'DNS-01' } else { 'HTTP-01' })"
    if ([int]$Cert.ChallengeType -eq $Script:Ch_Dns01) {
        Write-Host "    DNS provider    : $(if ([int]$Cert.DnsProvider -eq $Script:Dns_Cloudflare) { 'Cloudflare' } else { 'Manual' })"
    }
    Write-Host "    IIS site        : $(if ($Cert.IisSiteName) { $Cert.IisSiteName } else { '-' })"
    Write-Host "    IIS friendly name: $(if (-not [string]::IsNullOrWhiteSpace((Get-PropValue -Obj $Cert -Name 'FriendlyName'))) { $Cert.FriendlyName } else { '-' })"
    $remoteTargets = @(Get-PropValue -Obj $Cert -Name 'RemoteTargets')
    if ($remoteTargets.Count -gt 0) {
        Write-Host "    Remote IIS servers:"
        foreach ($rt in $remoteTargets) {
            $state = if ($rt.LastError) { "ERROR: $($rt.LastError)" } elseif ($rt.LastDeployed) { "last deployed $($rt.LastDeployed)" } else { 'not yet deployed' }
            Write-Host ("      - {0} (sites: {1}) - {2}" -f $rt.Host, (@($rt.SiteNames) -join ', '), $state)
        }
    }
    Write-Host "    Bind to IIS     : $($Cert.BindToIis)"
    Write-Host "    Auto-renew      : $($Cert.AutoRenew) (within $($Cert.RenewalDaysBeforeExpiry) days)"
    Write-Host "    Status          : $st"
    Write-Host "    Thumbprint      : $(if ($Cert.Thumbprint) { $Cert.Thumbprint } else { '-' })"
    Write-Host "    Not before      : $(if ($Cert.NotBefore) { $Cert.NotBefore } else { '-' })"
    Write-Host "    Not after       : $(if ($Cert.NotAfter) { $Cert.NotAfter } else { '-' })"
    Write-Host "    Last renewed    : $(if ($Cert.LastRenewed) { $Cert.LastRenewed } else { '-' })"
    Write-Host "    Deployment tasks: $((@($Cert.DeploymentTasks)).Count)"
    if ($Cert.LastError) { Write-Host "    Last error      : $($Cert.LastError)" -ForegroundColor Red }
    Write-Host ''
}

function Select-CertificateInteractive {
    param([string]$Prompt = 'Select a certificate')
    $all = @(Get-AllCertificates)
    if ($all.Count -eq 0) { Write-Host "  No certificates available." -ForegroundColor DarkGray; return $null }
    for ($i = 0; $i -lt $all.Count; $i++) {
        Write-Host ("   {0}. {1}  ({2})" -f ($i + 1), $all[$i].PrimaryDomain, $all[$i].Name)
    }
    $sel = Read-Host $Prompt
    if ($sel -match '^\d+$' -and [int]$sel -ge 1 -and [int]$sel -le $all.Count) { return $all[[int]$sel - 1] }
    Write-Host "  Invalid selection." -ForegroundColor Yellow
    return $null
}

#endregion

#region ----------------------------------------------------------- Interactive flows

function Invoke-NewCertificateWizard {
    Write-Host "`n  --- New certificate ---`n" -ForegroundColor Cyan
    $settings = Get-Settings
    $cert = New-ManagedCertificate

    $primary = Read-Default -Prompt '  Primary domain (e.g. www.example.com)'
    if ([string]::IsNullOrWhiteSpace($primary)) { Write-Host "  A primary domain is required." -ForegroundColor Yellow; return }
    $cert.PrimaryDomain = $primary.Trim()

    $sanRaw = Read-Default -Prompt '  Additional SAN domains (comma-separated, optional)'
    if ($sanRaw) { $cert.SubjectAlternativeNames = @($sanRaw -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

    $cert.Name = Read-Default -Prompt '  Friendly name' -Default $cert.PrimaryDomain
    $cert.ContactEmail = Read-Default -Prompt '  Contact email' -Default $settings.ContactEmail

    $isWildcard = (@(Get-AllDomains -Cert $cert) | Where-Object { $_.StartsWith('*.') }).Count -gt 0
    if ($isWildcard) {
        Write-Host "  Wildcard domain detected -> DNS-01 validation required." -ForegroundColor Yellow
        $cert.ChallengeType = $Script:Ch_Dns01
    } else {
        $ch = Read-Default -Prompt '  Validation: (1) HTTP-01  (2) DNS-01' -Default '1'
        $cert.ChallengeType = if ($ch -eq '2') { $Script:Ch_Dns01 } else { $Script:Ch_Http01 }
    }

    if ([int]$cert.ChallengeType -eq $Script:Ch_Dns01) {
        $dp = Read-Default -Prompt '  DNS provider: (1) Manual  (2) Cloudflare' -Default '2'
        if ($dp -eq '1') {
            $cert.DnsProvider = $Script:Dns_Manual
        } else {
            $cert.DnsProvider = $Script:Dns_Cloudflare
            $token = Read-Host '  Cloudflare API token (Zone:DNS:Edit)'
            $cert.DnsCredentialProtected = Protect-Secret -Plaintext $token
        }
    } else {
        # HTTP-01: pick an IIS site (which provides the web root) or a manual web root.
        $sites = @(Get-IisSites)
        if ($sites.Count -gt 0) {
            Write-Host "  IIS sites:" -ForegroundColor DarkGray
            for ($i = 0; $i -lt $sites.Count; $i++) { Write-Host ("    {0}. {1}  ({2})" -f ($i + 1), $sites[$i].Name, $sites[$i].PhysicalPath) }
            $sel = Read-Default -Prompt '  Choose an IIS site number, or blank to enter a web root path'
            if ($sel -match '^\d+$' -and [int]$sel -ge 1 -and [int]$sel -le $sites.Count) {
                $cert.IisSiteName = $sites[[int]$sel - 1].Name
                $cert.WebRootPath = $sites[[int]$sel - 1].PhysicalPath
            }
        }
        if (-not $cert.IisSiteName) {
            $cert.WebRootPath = Read-Default -Prompt '  Web root path (served on http://<domain>/)'
        }
    }

    # IIS binding
    $sitesForBind = @(Get-IisSites)
    if ($sitesForBind.Count -gt 0) {
        $cert.BindToIis = Read-YesNo -Prompt '  Bind the certificate to an IIS site?' -Default $true
        if ($cert.BindToIis -and -not $cert.IisSiteName) {
            for ($i = 0; $i -lt $sitesForBind.Count; $i++) { Write-Host ("    {0}. {1}" -f ($i + 1), $sitesForBind[$i].Name) }
            $sel = Read-Default -Prompt '  IIS site to bind'
            if ($sel -match '^\d+$' -and [int]$sel -ge 1 -and [int]$sel -le $sitesForBind.Count) {
                $cert.IisSiteName = $sitesForBind[[int]$sel - 1].Name
            }
        }
    } else {
        $cert.BindToIis = $false
    }

    # Friendly name shown in IIS's Server Certificates list (optional).
    $friendly = Read-Default -Prompt '  Certificate name in IIS (friendly name, optional)'
    if (-not [string]::IsNullOrWhiteSpace($friendly)) { $cert.FriendlyName = $friendly.Trim() }

    # Remote IIS servers (WinRM). This instance renews and re-deploys to each on
    # every renewal; it connects as the current identity (domain account/Kerberos).
    if (Read-YesNo -Prompt '  Deploy to remote IIS server(s) over WinRM?' -Default $false) {
        $targets = @()
        do {
            $rHost = Read-Default -Prompt '    Remote host name (blank to finish)'
            if ([string]::IsNullOrWhiteSpace($rHost)) { break }
            $sitesRaw = Read-Default -Prompt '    Remote IIS site name(s), comma-separated'
            $rSites = @($sitesRaw -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            $useSsl = Read-YesNo -Prompt '    Use WinRM over HTTPS (port 5986)?' -Default $true
            $port   = if ($useSsl) { 5986 } else { 5985 }
            $targets += (New-RemoteIisTarget -HostName $rHost.Trim() -WinRmPort $port -UseSsl $useSsl -SiteNames $rSites)
        } while ($true)
        $cert.RemoteTargets = @($targets)
    }

    $cert.AutoRenew = Read-YesNo -Prompt '  Enable automatic renewal?' -Default $true

    Save-AndRequestCertificate -Cert $cert -Settings $settings
}

function Save-AndRequestCertificate {
    param($Cert, $Settings)
    Set-Certificate -Certificate $Cert
    Write-Host ''
    if (Read-YesNo -Prompt "  Request the certificate now ($(if ([int]$Settings.Environment -eq $Script:Env_Production) { 'PRODUCTION' } else { 'STAGING' }))?" -Default $true) {
        try { Invoke-RequestAndDeploy -Cert $Cert -EnvId ([int]$Settings.Environment) | Out-Null }
        catch { Write-Host "  Request failed: $($_.Exception.Message)" -ForegroundColor Red }
    } else {
        Write-Host "  Saved. You can request it later from the menu." -ForegroundColor DarkGray
    }
}

function Invoke-SettingsMenu {
    while ($true) {
        $s = Get-Settings
        Write-Host "`n  --- Settings ---" -ForegroundColor Cyan
        Write-Host "   1. Environment        : $(if ([int]$s.Environment -eq $Script:Env_Production) { 'Production' } else { 'Staging' })"
        Write-Host "   2. Contact email      : $(if ($s.ContactEmail) { $s.ContactEmail } else { '(not set)' })"
        Write-Host "   3. Auto-renewal       : $($s.EnableAutoRenewal)"
        Write-Host "   4. Notifications..."
        Write-Host "   0. Back"
        switch (Read-Host '  Choose') {
            '1' { $s.Environment = if ([int]$s.Environment -eq $Script:Env_Production) { $Script:Env_Staging } else { $Script:Env_Production }; Save-Settings $s; Write-Host "  Environment set to $(if ([int]$s.Environment -eq $Script:Env_Production) {'Production'} else {'Staging'})." -ForegroundColor Green }
            '2' { $s.ContactEmail = Read-Default -Prompt '  Contact email' -Default $s.ContactEmail; Save-Settings $s }
            '3' { $s.EnableAutoRenewal = Read-YesNo -Prompt '  Enable automatic renewal?' -Default $s.EnableAutoRenewal; Save-Settings $s }
            '4' { Invoke-NotificationsMenu }
            '0' { return }
            default { }
        }
    }
}

function Invoke-NotificationsMenu {
    $s = Get-Settings
    $n = $s.Notifications
    Write-Host "`n  --- Notifications ---" -ForegroundColor Cyan
    $n.NotifyOnSuccess = Read-YesNo -Prompt '  Notify on success?' -Default $n.NotifyOnSuccess
    $n.NotifyOnFailure = Read-YesNo -Prompt '  Notify on failure?' -Default $n.NotifyOnFailure

    $n.WebhookUrl = Read-Default -Prompt '  Webhook URL (Slack/Teams/Discord/custom; blank to disable)' -Default $n.WebhookUrl

    $n.EmailEnabled = Read-YesNo -Prompt '  Enable email (SMTP) notifications?' -Default $n.EmailEnabled
    if ($n.EmailEnabled) {
        $n.SmtpHost    = Read-Default -Prompt '  SMTP host' -Default $n.SmtpHost
        $n.SmtpPort    = [int](Read-Default -Prompt '  SMTP port' -Default $n.SmtpPort)
        $n.SmtpUseSsl  = Read-YesNo -Prompt '  Use SSL/TLS?' -Default $n.SmtpUseSsl
        $n.SmtpUsername = Read-Default -Prompt '  SMTP username (blank for none)' -Default $n.SmtpUsername
        if ($n.SmtpUsername) {
            $pw = Read-Host '  SMTP password (stored encrypted; blank to keep existing)'
            if ($pw) { $n.SmtpPasswordProtected = Protect-Secret -Plaintext $pw }
        }
        $n.FromAddress = Read-Default -Prompt '  From address' -Default $n.FromAddress
        $n.ToAddress   = Read-Default -Prompt '  To address' -Default $n.ToAddress
    }
    Save-Settings $s
    Write-Host "  Notification settings saved." -ForegroundColor Green
}

function Invoke-RenewMenu {
    $c = Select-CertificateInteractive -Prompt '  Certificate to renew'
    if (-not $c) { return }
    try { Invoke-RequestAndDeploy -Cert $c -EnvId ([int](Get-Settings).Environment) | Out-Null }
    catch { Write-Host "  Renewal failed: $($_.Exception.Message)" -ForegroundColor Red }
}

function Invoke-BindMenu {
    $c = Select-CertificateInteractive -Prompt '  Certificate to bind'
    if (-not $c) { return }
    $sites = @(Get-IisSites)
    if ($sites.Count -eq 0) { Write-Host "  No IIS sites found." -ForegroundColor Yellow; return }
    for ($i = 0; $i -lt $sites.Count; $i++) { Write-Host ("    {0}. {1}" -f ($i + 1), $sites[$i].Name) }
    $sel = Read-Host '  IIS site number'
    if ($sel -match '^\d+$' -and [int]$sel -ge 1 -and [int]$sel -le $sites.Count) {
        $site = $sites[[int]$sel - 1].Name
        try {
            $c.IisSiteName = $site; $c.BindToIis = $true
            Invoke-IisBind -Cert $c -SiteName $site
            Set-Certificate -Certificate $c
            Write-Host "  Bound to '$site'." -ForegroundColor Green
        } catch { Write-Host "  Bind failed: $($_.Exception.Message)" -ForegroundColor Red }
    }
}

function Invoke-ExportMenu {
    $c = Select-CertificateInteractive -Prompt '  Certificate to export'
    if (-not $c) { return }
    $type = Read-Default -Prompt '  Export as (1) PFX  (2) PEM' -Default '1'
    try {
        if ($type -eq '2') {
            $dir = Read-Default -Prompt '  Output directory'
            Export-IssuedCertificate -Cert $c -Type Pem -Destination $dir
        } else {
            $path = Read-Default -Prompt '  Output .pfx path'
            $pw   = Read-Host '  PFX password (blank for none)'
            Export-IssuedCertificate -Cert $c -Type Pfx -Destination $path -Password $pw
        }
    } catch { Write-Host "  Export failed: $($_.Exception.Message)" -ForegroundColor Red }
}

function Invoke-RemoveMenu {
    $c = Select-CertificateInteractive -Prompt '  Certificate to remove'
    if (-not $c) { return }
    if (Read-YesNo -Prompt "  Remove '$($c.PrimaryDomain)' from management? (does not revoke)" -Default $false) {
        Remove-Certificate -CertId $c.Id
        Write-Host "  Removed." -ForegroundColor Green
    }
}

function Invoke-ImportMenu {
    Write-Host "`n  --- Import / rescan existing certificates ---" -ForegroundColor Cyan
    Write-Host "  Scans LocalMachine\My for issued certificates not yet tracked here" -ForegroundColor DarkGray
    Write-Host "  (e.g. created by the .NET app or a previous tool)." -ForegroundColor DarkGray
    $inclAll = Read-YesNo -Prompt "  Also import certificates NOT issued by Let's Encrypt?" -Default $false
    try {
        $imported = @(Import-ExistingCertificates -IncludeNonLetsEncrypt:$inclAll)
        if ($imported.Count -gt 0) { Show-CertificateTable }
    } catch { Write-Host "  Import failed: $($_.Exception.Message)" -ForegroundColor Red }
}

function Invoke-TaskMenu {
    Write-Host "`n  --- Renewal scheduled task ---" -ForegroundColor Cyan
    Write-Host "   Current state: $(Get-RenewalTaskState)"
    Write-Host "   1. Install / update task (every 12h, as SYSTEM)"
    Write-Host "   2. Remove task"
    Write-Host "   3. Run renewal now"
    Write-Host "   0. Back"
    switch (Read-Host '  Choose') {
        '1' { try { Install-RenewalTask } catch { Write-Host "  $($_.Exception.Message)" -ForegroundColor Red } }
        '2' { try { Uninstall-RenewalTask } catch { Write-Host "  $($_.Exception.Message)" -ForegroundColor Red } }
        '3' { Invoke-RenewDue }
        default { }
    }
}

function Start-Menu {
    Write-Banner
    if (-not (Test-IsAdmin)) {
        Write-Host "`n  NOTE: not running as Administrator. Issuing, cert-store, IIS and" -ForegroundColor Yellow
        Write-Host "        scheduled-task actions need elevation. Re-launch elevated for those." -ForegroundColor Yellow
    }
    while ($true) {
        $s = Get-Settings
        Write-Host "`n  Environment: " -NoNewline
        Write-Host ("{0}" -f $(if ([int]$s.Environment -eq $Script:Env_Production) { 'PRODUCTION' } else { 'STAGING' })) `
            -ForegroundColor $(if ([int]$s.Environment -eq $Script:Env_Production) { 'Green' } else { 'Yellow' })
        Write-Host "  Renewal task: $(Get-RenewalTaskState)" -ForegroundColor DarkGray
        Write-Host ''
        Write-Host "   1. List certificates"
        Write-Host "   2. New certificate"
        Write-Host "   3. Certificate details"
        Write-Host "   4. Renew a certificate"
        Write-Host "   5. Renew all due now"
        Write-Host "   6. Bind to IIS"
        Write-Host "   7. Export certificate"
        Write-Host "   8. Remove certificate"
        Write-Host "   9. Settings"
        Write-Host "  10. Renewal scheduled task"
        Write-Host "  11. Import / rescan existing certificates"
        Write-Host "   0. Exit"
        switch (Read-Host "`n  Choose") {
            '1'  { Show-CertificateTable }
            '2'  { Invoke-NewCertificateWizard }
            '3'  { $c = Select-CertificateInteractive; if ($c) { Show-CertificateDetail -Cert $c } }
            '4'  { Invoke-RenewMenu }
            '5'  { Invoke-RenewDue }
            '6'  { Invoke-BindMenu }
            '7'  { Invoke-ExportMenu }
            '8'  { Invoke-RemoveMenu }
            '9'  { Invoke-SettingsMenu }
            '10' { Invoke-TaskMenu }
            '11' { Invoke-ImportMenu }
            '0'  { Write-Host "  Goodbye.`n"; return }
            default { Write-Host "  Unknown choice." -ForegroundColor Yellow }
        }
    }
}

#endregion

#region ----------------------------------------------------------- Non-interactive dispatch

function Show-Help {
@"
LetsSSL4Windows (PowerShell edition)

  Interactive:   .\LetsSSL4Windows.ps1
  Commands:      .\LetsSSL4Windows.ps1 -Command <verb> [options]

  Verbs:
    List                         List managed certificates.
    Show       -Id <sel>         Show details for a certificate.
    New        ...               Create + request a certificate (see options).
    Renew      -Id <sel>         Re-issue a specific certificate.
    RenewDue                     Renew everything currently due (scheduled task uses this).
    Bind       -Id <sel> -IisSite <name>   Bind an issued cert to an IIS site.
    Export     -Id <sel> -ExportType Pfx|Pem -OutPath <path> [-PfxPassword <pw>]
    Remove     -Id <sel>         Stop managing a certificate (does not revoke).
    Import     [-IncludeNonLetsEncrypt]   Add certs in LocalMachine\My not yet tracked.
    Settings   [-Environment Staging|Production] [-ContactEmail <addr>]
    InstallTask | UninstallTask  Manage the renewal scheduled task.
    Help                         Show this help.

  New options:
    -Domain <fqdn> [-SAN a,b] -Name <text> -ContactEmail <addr>
    -ChallengeType Http01|Dns01
    -DnsProvider Manual|Cloudflare -DnsCredential <token>
    -IisSite <name> | -WebRoot <path>
    -FriendlyName <text>   Name shown for the certificate in IIS
    -RemoteTarget "host=web2;sites=Default Web Site,api;port=5986;ssl=1"
                           Deploy to a remote IIS server over WinRM (repeatable).
                           Requires WinRM on the target and that this account
                           (ideally a domain service account) is admin there.
    -NoBind  -NoAutoRenew  -RenewalDays <n>

  Data store: %ProgramData%\$($Script:AppName)
"@ | Write-Host
}

function Invoke-NewFromParams {
    $settings = Get-Settings
    if (-not $Domain) { throw "-Domain is required for -Command New." }
    $cert = New-ManagedCertificate
    $cert.PrimaryDomain = $Domain.Trim()
    if ($SAN) { $cert.SubjectAlternativeNames = @($SAN | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    $cert.Name = if ($Name) { $Name } else { $Domain }
    $cert.ContactEmail = if ($ContactEmail) { $ContactEmail } else { $settings.ContactEmail }
    $cert.RenewalDaysBeforeExpiry = $RenewalDays
    $cert.AutoRenew = -not $NoAutoRenew

    $isWildcard = (@(Get-AllDomains -Cert $cert) | Where-Object { $_.StartsWith('*.') }).Count -gt 0
    if ($ChallengeType) { $cert.ChallengeType = if ($ChallengeType -eq 'Dns01') { $Script:Ch_Dns01 } else { $Script:Ch_Http01 } }
    if ($isWildcard) { $cert.ChallengeType = $Script:Ch_Dns01 }

    if ([int]$cert.ChallengeType -eq $Script:Ch_Dns01) {
        $cert.DnsProvider = if ($DnsProvider -eq 'Cloudflare') { $Script:Dns_Cloudflare } else { $Script:Dns_Manual }
        if ([int]$cert.DnsProvider -eq $Script:Dns_Cloudflare) {
            if (-not $DnsCredential) { throw "-DnsCredential (Cloudflare API token) is required for Cloudflare DNS-01." }
            $cert.DnsCredentialProtected = Protect-Secret -Plaintext $DnsCredential
        }
    } else {
        if ($WebRoot) { $cert.WebRootPath = $WebRoot }
        if ($IisSite) { $cert.IisSiteName = $IisSite }
    }
    if ($IisSite) { $cert.IisSiteName = $IisSite }
    if ($FriendlyName) { $cert.FriendlyName = $FriendlyName.Trim() }
    if ($RemoteTarget) { $cert.RemoteTargets = @($RemoteTarget | ForEach-Object { ConvertTo-RemoteIisTarget -Spec $_ } | Where-Object { $_ }) }
    $cert.BindToIis = (-not $NoBind) -and [bool]$cert.IisSiteName

    Set-Certificate -Certificate $cert
    Invoke-RequestAndDeploy -Cert $cert -EnvId ([int]$settings.Environment) | Out-Null
}

function Invoke-CommandDispatch {
    Initialize-Paths
    switch ($Command) {
        'List' { Show-CertificateTable }
        'Show' {
            if (-not $Id) { throw "-Id is required for -Command Show." }
            Show-CertificateDetail -Cert (Resolve-Certificate -Selector $Id)
        }
        'New' { Invoke-NewFromParams }
        'Renew' {
            if (-not $Id) { throw "-Id is required for -Command Renew." }
            $c = Resolve-Certificate -Selector $Id
            Invoke-RequestAndDeploy -Cert $c -EnvId ([int](Get-Settings).Environment) | Out-Null
        }
        'RenewDue' { Invoke-RenewDue }
        'Import' { Import-ExistingCertificates -IncludeNonLetsEncrypt:$IncludeNonLetsEncrypt | Out-Null }
        'Bind' {
            if (-not $Id -or -not $IisSite) { throw "-Id and -IisSite are required for -Command Bind." }
            $c = Resolve-Certificate -Selector $Id
            $c.IisSiteName = $IisSite; $c.BindToIis = $true
            Invoke-IisBind -Cert $c -SiteName $IisSite
            Set-Certificate -Certificate $c
        }
        'Export' {
            if (-not $Id -or -not $OutPath) { throw "-Id and -OutPath are required for -Command Export." }
            $c = Resolve-Certificate -Selector $Id
            Export-IssuedCertificate -Cert $c -Type $ExportType -Destination $OutPath -Password $PfxPassword
        }
        'Remove' {
            if (-not $Id) { throw "-Id is required for -Command Remove." }
            $c = Resolve-Certificate -Selector $Id
            Remove-Certificate -CertId $c.Id
            Write-Log "Removed '$($c.PrimaryDomain)' from management." 'OK'
        }
        'Settings' {
            $s = Get-Settings
            if ($Environment)  { $s.Environment  = if ($Environment -eq 'Production') { $Script:Env_Production } else { $Script:Env_Staging } }
            if ($ContactEmail) { $s.ContactEmail = $ContactEmail }
            Save-Settings $s
            Write-Log "Settings updated. Environment=$(if ([int]$s.Environment -eq $Script:Env_Production) {'Production'} else {'Staging'}), Contact=$($s.ContactEmail)" 'OK'
        }
        'InstallTask'   { Install-RenewalTask }
        'UninstallTask' { Uninstall-RenewalTask }
        'Help'          { Show-Help }
        default         { Show-Help }
    }
}

#endregion

#region ----------------------------------------------------------- Entry point

# Entry point. Skipped when the script is dot-sourced (the module wrapper and
# the Pester tests load the functions without launching the UI) or when
# LETSSSL4WINDOWS_NORUN is set.
if ($MyInvocation.InvocationName -ne '.' -and -not $env:LETSSSL4WINDOWS_NORUN) {
    try {
        # Require elevation; relaunch through UAC if we're not admin yet.
        if (Invoke-SelfElevation) { exit 0 }
        Initialize-Paths
        if ($Command -eq 'Menu') { Start-Menu }
        else { Invoke-CommandDispatch }
    } catch {
        Write-Log $_.Exception.Message 'ERROR'
        if (-not $Unattended) { Write-Host "  $($_.Exception.Message)" -ForegroundColor Red }
        exit 1
    }
}

#endregion
