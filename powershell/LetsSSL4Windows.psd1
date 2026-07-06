@{
    RootModule        = 'LetsSSL4Windows.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'afd14d8a-62f0-4175-9266-e06714ae4c9c'
    Author            = 'MikeYeg and contributors'
    CompanyName       = 'LetsSSL4Windows'
    Copyright         = '(c) MikeYeg and contributors. MIT License.'
    Description       = 'Free, console-driven Let''s Encrypt certificate manager for Windows/IIS. Requests, installs, binds, deploys and auto-renews ACME certificates (HTTP-01/DNS-01, wildcards) via the Posh-ACME module. Posh-ACME is installed automatically on first issuance if missing.'

    PowerShellVersion = '5.1'
    # Posh-ACME is a runtime dependency. It is intentionally NOT listed in
    # RequiredModules (which would block import if absent); the script installs
    # it on demand. For unattended SYSTEM renewal, pre-install it machine-wide:
    #   Install-Module Posh-ACME -Scope AllUsers -Force

    FunctionsToExport = @(
        'New-ManagedCertificate'
        'Get-AllCertificates'
        'Set-Certificate'
        'Remove-Certificate'
        'Resolve-Certificate'
        'Import-ExistingCertificates'
        'Get-AllDomains'
        'Get-CertStatus'
        'Get-StatusText'
        'Test-IsDueForRenewal'
        'Get-Settings'
        'Save-Settings'
        'Protect-Secret'
        'Unprotect-Secret'
        'Invoke-RequestAndDeploy'
        'Invoke-RenewDue'
        'Get-IisSites'
        'Get-IisSitePhysicalPath'
        'Invoke-IisBind'
        'Export-IssuedCertificate'
        'Install-RenewalTask'
        'Uninstall-RenewalTask'
        'Get-RenewalTaskState'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags        = @('LetsEncrypt', 'ACME', 'SSL', 'TLS', 'Certificate', 'IIS', 'Windows', 'Posh-ACME')
            LicenseUri  = 'https://opensource.org/licenses/MIT'
            ProjectUri  = 'https://github.com/MikeYeg/LetsSSL4Windows'
            ReleaseNotes = 'PowerShell edition: console UI + module API with full feature parity (HTTP-01/DNS-01, wildcards, IIS SNI binding, deployment tasks, notifications, DPAPI secrets, scheduled-task renewal).'
        }
    }
}
