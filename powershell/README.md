# LetsSSL4Windows — PowerShell edition

A single, self-contained PowerShell port of [LetsSSL4Windows](../README.md). It
delivers the same everyday workflow — request, install, bind, deploy, and
auto-renew Let's Encrypt certificates on Windows/IIS — but as a **console-driven
tool** instead of a WPF app. No compiler, no installer: just one `.ps1`.

ACME issuance is handled by the mature, MIT-licensed
[Posh-ACME](https://github.com/rmbolger/Posh-ACME) module, which the script
installs automatically if it is missing.

> **Download:** the PowerShell edition ships in the **same GitHub Release** as the
> desktop app. Each release contains both the installer (`LetsSSL4Windows-Setup-<version>.exe`)
> and the script (`LetsSSL4Windows-<version>.ps1`, plus a
> `LetsSSL4Windows-PowerShell-<version>.zip` with the module, README, and tests).
> Pick whichever edition you prefer — they share the same data store.

## Feature parity

| Capability | WPF app | PowerShell edition |
| --- | :---: | :---: |
| Let's Encrypt issuance (ACME, staging + production) | ✅ | ✅ |
| HTTP-01 validation | ✅ | ✅ (Posh-ACME `WebRoot`) |
| DNS-01 validation | ✅ | ✅ (Cloudflare, Manual) |
| Wildcard certificates | ✅ | ✅ (DNS-01) |
| Multi-domain (SAN) certs | ✅ | ✅ |
| Install to `LocalMachine\My` (exportable) | ✅ | ✅ |
| Auto-bind to IIS (SNI) | ✅ | ✅ (`WebAdministration`) |
| Deployment tasks (PFX / PEM / run script) | ✅ | ✅ |
| On-demand export (PFX / PEM) | ✅ | ✅ |
| Email + webhook notifications | ✅ | ✅ |
| Encrypted secrets (DPAPI, LocalMachine) | ✅ | ✅ (same `DPAPI:` format) |
| Unattended renewal | ✅ (Windows Service) | ✅ (Scheduled Task) |
| CA-suggested renewal (ARI, RFC 9773) | ✅ | ✅ |
| Self-elevation (UAC) | ✅ (app manifest) | ✅ (relaunch via UAC) |
| Import existing certs from the store | ➖ | ✅ (`Import` / rescan) |
| Reusable PowerShell module API | ➖ | ✅ (`.psm1` + `.psd1`) |
| UI | WPF dashboard | **Interactive console menu + commands** |

The data store schema and location are identical, so the two editions can share
one store: `%ProgramData%\LetsSSL4Windows` (`appsettings.json`,
`certificates.json`, `lastrun.json`, `pfx\`, `logs\`). The ACME account/keys
live in `%ProgramData%\LetsSSL4Windows\posh-acme` so the SYSTEM scheduled task
and your interactive session use the same account.

Activity is also written to the **Windows Event Log** (Event Viewer → Windows
Logs → Application, source **`LetsSSL4Windows`**) in addition to the monthly
`logs\activity-*.log` file. The event source is registered automatically the
first time the script runs elevated (creating it needs administrator rights); if
it can't be created, logging falls back to the file/console only.

## Requirements

- Windows 10/11 or Windows Server 2016+
- Windows PowerShell 5.1 **or** PowerShell 7+
- **Administrator rights** for issuing, cert-store, IIS, and scheduled-task
  actions. You don't have to launch an elevated prompt yourself — the script
  **auto-elevates**: if it isn't already running as admin it relaunches itself
  through a UAC prompt, passing your arguments through. (The unattended renewal
  task runs as SYSTEM, so it's already elevated and never prompts.)
- For HTTP-01: the domain must resolve to this server and be reachable on port 80
- Internet access to install Posh-ACME the first time (or pre-install it)

## Quick start

```powershell
# Allow the script to run in this session (or sign it / adjust your policy)
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

# Launch the interactive menu. If you're not already elevated, it will request
# elevation via UAC and relaunch itself in an elevated console.
.\LetsSSL4Windows.ps1
```

First run, recommended flow:

1. Open **Settings → Environment** and keep it on **Staging** (untrusted test
   certs, generous rate limits) while you verify everything works.
2. Set your **contact email** in Settings.
3. Choose **New certificate**, pick an IIS site (web root auto-fills), confirm
   the domain, and request it.
4. Once staging works end-to-end, switch the environment to **Production** and
   re-issue for a real, browser-trusted certificate.

## Non-interactive commands

Drive any single action with `-Command <verb>` — ideal for automation:

```powershell
# List / inspect
.\LetsSSL4Windows.ps1 -Command List
.\LetsSSL4Windows.ps1 -Command Show -Id www.example.com

# HTTP-01 + bind to IIS (optionally name it in IIS with -FriendlyName)
.\LetsSSL4Windows.ps1 -Command New -Domain www.example.com -SAN example.com `
    -ChallengeType Http01 -IisSite "Default Web Site" -FriendlyName "www.example.com (Let's Encrypt)"

# DNS-01 wildcard via Cloudflare
.\LetsSSL4Windows.ps1 -Command New -Domain *.example.com -SAN example.com `
    -ChallengeType Dns01 -DnsProvider Cloudflare -DnsCredential "<cf-api-token>"

# Renew one, or everything due
.\LetsSSL4Windows.ps1 -Command Renew -Id www.example.com
.\LetsSSL4Windows.ps1 -Command RenewDue

# Bind / export
.\LetsSSL4Windows.ps1 -Command Bind   -Id www.example.com -IisSite "Default Web Site"
.\LetsSSL4Windows.ps1 -Command Export -Id www.example.com -ExportType Pem -OutPath C:\certs
.\LetsSSL4Windows.ps1 -Command Export -Id www.example.com -ExportType Pfx -OutPath C:\certs\site.pfx -PfxPassword "secret"

# Back up / restore the whole data store (settings, certs, account keys, PFXs)
.\LetsSSL4Windows.ps1 -Command Backup  -OutPath C:\backups\letsssl.zip
.\LetsSSL4Windows.ps1 -Command Restore -InPath  C:\backups\letsssl.zip

# Settings + scheduled task
.\LetsSSL4Windows.ps1 -Command Settings -Environment Production -ContactEmail admin@example.com
.\LetsSSL4Windows.ps1 -Command InstallTask
.\LetsSSL4Windows.ps1 -Command Help
```

Run `-Command Help` for the full option list.

## Automatic renewal (Scheduled Task)

`InstallTask` registers a Windows Scheduled Task named **"LetsSSL4Windows
Renewal"** that runs `-Command RenewDue` every 12 hours as **SYSTEM** with
highest privileges. It re-issues any certificate inside its renewal window
(default 30 days before expiry). Remove it with `-Command UninstallTask`, or
manage it from the menu (**Renewal scheduled task**).

Before each run, `RenewDue` fetches the CA's **ACME Renewal Information**
([ARI, RFC 9773](https://www.rfc-editor.org/rfc/rfc9773.html)) for every issued
certificate. When the CA suggests renewing **earlier** than the fixed window (for
example ahead of a mass revocation), the certificate becomes due at the suggested
time — so it's replaced before it's revoked rather than silently going invalid.
ARI is advisory and only ever pulls renewal forward; if the CA doesn't support it
or the lookup fails, the date-based schedule applies. The module also exposes
`Get-AcmeRenewalInfo`, `Get-AriCertId`, and `Update-RenewalInfo` for scripting.

> **Manual DNS** cannot run unattended — it needs you to create the TXT record
> by hand. Use **Cloudflare** (or another automated provider) for hands-off
> wildcard renewal.

## DNS-01 and wildcards

Wildcard names (`*.example.com`) require DNS-01. Two providers ship today:

- **Cloudflare** — paste an API token with `Zone:DNS:Edit`. Records are created
  and cleaned up automatically (renews unattended). The token is stored
  DPAPI-encrypted. The New certificate wizard offers to validate the token via
  Cloudflare's token-verify endpoint (`Test-CloudflareToken`) when you enter it.
- **Manual** — Posh-ACME prints the exact TXT record and waits for you to create
  it (interactive only).

Posh-ACME supports [many more DNS providers](https://poshac.me/docs/v4/Plugins/).
To add one, extend `Invoke-RequestAndDeploy` with the provider's plugin name and
plugin args.

## Deployment tasks

After issuance you can run post-issue tasks (stored per certificate in
`certificates.json` under `DeploymentTasks`):

- **Export PFX** to a path, optionally re-encrypted with your own password.
- **Export PEM** (`fullchain.pem` + `privkey.pem`) for nginx/Apache/HAProxy.
- **Run script** — runs a `.ps1`/exe with `LETSSSL4WINDOWS_THUMBPRINT`,
  `LETSSSL4WINDOWS_DOMAIN`, `LETSSSL4WINDOWS_PFX_PATH`, and
  `LETSSSL4WINDOWS_PFX_PASSWORD` in the environment.

## Remote IIS deployment (WinRM)

A single instance can renew a certificate and push it to **multiple remote
Windows/IIS servers** on every renewal — this machine stays the single source of
truth. For each target it connects over **WinRM / PowerShell Remoting**, imports
the PFX into the remote `LocalMachine\My` (with the friendly name), and binds it
to the listed IIS sites with SNI.

```powershell
.\LetsSSL4Windows.ps1 -Command New -Domain www.example.com -IisSite "Default Web Site" `
    -RemoteTarget "host=web2.corp.local;sites=Default Web Site,api;port=5986;ssl=1" `
    -RemoteTarget "host=web3.corp.local;sites=Default Web Site"
```

`-RemoteTarget` is repeatable; the spec keys are `host` (required), `sites`
(comma-separated), `port` (default 5986), and `ssl` (`1`/`0`, default `1`). The
interactive **New certificate** wizard also prompts for remote servers, and
`-Command Show` lists each target's last-deployed / last-error state.

**Prerequisites**

- **WinRM enabled** on each target (`Enable-PSRemoting -Force`), reachable on the
  chosen port (5986 HTTPS recommended; open the firewall).
- The identity running the renewal — ideally a **domain service account** — must
  be a **local administrator** on each target. Authentication is **Kerberos**
  (the current identity); **no credentials are stored**. For non-domain targets
  you'd need to configure WinRM TrustedHosts/HTTPS yourself.
- A per-target failure is recorded (and logged) but never blocks the local
  install or the other targets.

## Notifications

Configure email (SMTP) and/or a webhook (Slack/Teams/Discord/custom) under
**Settings → Notifications**, and choose whether to notify on success, failure,
or both. The webhook payload is `{ text, status, domain, subject, body,
timestamp }`. The SMTP password is stored DPAPI-encrypted. Delivery is
best-effort and never blocks issuance.

## Notes & caveats

- **Posh-ACME visibility for SYSTEM**: the script installs Posh-ACME in
  `CurrentUser` scope. The SYSTEM scheduled task can only see it if it is
  installed machine-wide. For reliable unattended renewal, pre-install it for
  all users from an elevated prompt:
  `Install-Module Posh-ACME -Scope AllUsers -Force`.
- **Execution policy**: if the script is blocked, run it as
  `powershell -ExecutionPolicy Bypass -File .\LetsSSL4Windows.ps1` or unblock it
  with `Unblock-File .\LetsSSL4Windows.ps1`.
- **Secrets** are encrypted with DPAPI at the **LocalMachine** scope, so they are
  only decryptable on the machine that wrote them.

## Importing certificates created elsewhere

Because this edition shares the same store as the .NET app, certificates the
.NET app issued already appear in `List`. If a certificate exists in the Windows
store (`LocalMachine\My`) but **isn't** tracked in `certificates.json` — for
example a record got out of sync, or it was issued by another tool — use the
`Import` command (menu option **11**) to discover and adopt it:

```powershell
# Import untracked Let's Encrypt certificates from LocalMachine\My
.\LetsSSL4Windows.ps1 -Command Import

# Also include certificates not issued by Let's Encrypt
.\LetsSSL4Windows.ps1 -Command Import -IncludeNonLetsEncrypt
```

Import matches by **thumbprint** (so it never duplicates an already-tracked
cert), reads the domains from each certificate's subject/SAN, and records the
thumbprint and validity dates. Since the original request arguments (DNS
credentials, IIS site, deployment tasks) can't be recovered from the certificate
itself, imported records start with **auto-renew and IIS binding off** — review
them and turn those on, or set a DNS provider, before relying on unattended
renewal. You can `Export` an imported certificate immediately, since export reads
from the (exportable) store entry rather than a saved PFX.

## Use as a module

Besides the standalone script, the same functions are available as a module so
you can script against them:

```powershell
Import-Module .\LetsSSL4Windows.psd1

Get-Settings
$c = New-ManagedCertificate
$c.PrimaryDomain = 'www.example.com'
$c.IisSiteName   = 'Default Web Site'
Set-Certificate -Certificate $c
Invoke-RequestAndDeploy -Cert $c -EnvId 0    # 0 = staging, 1 = production

Get-AllCertificates | Where-Object { Test-IsDueForRenewal -Cert $_ }
```

The module (`LetsSSL4Windows.psm1` + `LetsSSL4Windows.psd1`) simply loads the
script's functions and exports the reusable API — `LetsSSL4Windows.ps1` remains
the single source of truth and stays runnable on its own. Exported functions
include `New-ManagedCertificate`, `Get-AllCertificates`, `Set-Certificate`,
`Resolve-Certificate`, `Get-CertStatus`, `Test-IsDueForRenewal`, `Get-Settings`,
`Save-Settings`, `Protect-Secret`/`Unprotect-Secret`, `Invoke-RequestAndDeploy`,
`Invoke-RenewDue`, `Get-IisSites`, `Invoke-IisBind`, `Export-IssuedCertificate`,
and `Install-RenewalTask`/`Uninstall-RenewalTask`.

## Tests

[Pester](https://pester.dev) v5 tests live in `tests\` and cover the
deterministic logic (data model, status/renewal computation, the JSON store,
settings, selectors, and — on Windows — the DPAPI round-trip). They redirect the
store into Pester's `TestDrive`, so the real `%ProgramData%` store is untouched.

```powershell
Install-Module Pester -MinimumVersion 5.0 -Scope CurrentUser   # once
Invoke-Pester -Path .\tests\LetsSSL4Windows.Tests.ps1
```

ACME issuance, IIS binding, and scheduled-task registration aren't unit-tested
because they need a live Windows + Let's Encrypt + IIS environment; verify those
against Let's Encrypt **staging**.

## License

MIT © MikeYEG and contributors. Let's Encrypt is a service of the Internet
Security Research Group (ISRG); certificates are subject to Let's Encrypt's
Subscriber Agreement and rate limits.
