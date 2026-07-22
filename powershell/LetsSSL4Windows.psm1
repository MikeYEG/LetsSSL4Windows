<#
    LetsSSL4Windows module wrapper.

    Loads the functions from the self-contained script (LetsSSL4Windows.ps1)
    without launching the console UI, and exports the reusable public API so you
    can script against it:

        Import-Module .\LetsSSL4Windows.psd1
        Get-Settings
        $c = New-ManagedCertificate
        $c.PrimaryDomain = 'www.example.com'; $c.IisSiteName = 'Default Web Site'
        Set-Certificate -Certificate $c
        Invoke-RequestAndDeploy -Cert $c -EnvId 0   # 0 = staging, 1 = production

    The .ps1 remains the single source of truth and stays runnable on its own.
#>

$scriptPath = Join-Path $PSScriptRoot 'LetsSSL4Windows.ps1'
if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "LetsSSL4Windows.ps1 was not found next to the module ($scriptPath)."
}

# Suppress the script's entry point while we dot-source it for its functions.
$env:LETSSSL4WINDOWS_NORUN = '1'
try {
    . $scriptPath
} finally {
    Remove-Item Env:LETSSSL4WINDOWS_NORUN -ErrorAction SilentlyContinue
}

$Public = @(
    # Data model & store
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
    # Settings
    'Get-Settings'
    'Save-Settings'
    # Secrets
    'Protect-Secret'
    'Unprotect-Secret'
    # Notifications
    'Send-TestNotification'
    # Issuance / renewal
    'Invoke-RequestAndDeploy'
    'Invoke-RenewDue'
    # IIS
    'Get-IisSites'
    'Get-IisSitePhysicalPath'
    'Invoke-IisBind'
    # Remote IIS (WinRM)
    'New-RemoteIisTarget'
    'Invoke-RemoteIisDeploy'
    # Export
    'Export-IssuedCertificate'
    # Scheduled task
    'Install-RenewalTask'
    'Uninstall-RenewalTask'
    'Get-RenewalTaskState'
)

Export-ModuleMember -Function $Public
