<div align="center">

# 🔒 LetsSSL4Windows

### A free, open-source SSL/TLS certificate manager for Windows

*A no-strings, MIT-licensed alternative to Certify The Web — request, install, bind, and auto-renew Let's Encrypt certificates. Available as an intuitive **desktop app** and a **PowerShell console edition**, both shipped in every release.*

<br/>

[![CI](https://github.com/MikeYEG/LetsSSL4Windows/actions/workflows/ci.yml/badge.svg)](https://github.com/MikeYEG/LetsSSL4Windows/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/github/license/MikeYEG/LetsSSL4Windows?color=blue)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20%7C%20Server-0078D6?logo=windows&logoColor=white)](#prerequisites)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](#contributing)

[![GitHub stars](https://img.shields.io/github/stars/MikeYEG/LetsSSL4Windows?style=social)](https://github.com/MikeYEG/LetsSSL4Windows/stargazers)
[![GitHub issues](https://img.shields.io/github/issues/MikeYEG/LetsSSL4Windows)](https://github.com/MikeYEG/LetsSSL4Windows/issues)

<br/>

**❤️ If LetsSSL4Windows saves you a license fee, consider supporting development:**

[![Sponsor on GitHub](https://img.shields.io/badge/Sponsor-%E2%9D%A4-db61a2?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/MikeYEG)
&nbsp;
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-FFDD00?logo=buymeacoffee&logoColor=black)](https://www.buymeacoffee.com/MikeYEG)

<a href="https://www.buymeacoffee.com/MikeYEG" target="_blank">
  <img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" height="44" />
</a>

</div>

---

## Table of contents

- [Why LetsSSL4Windows?](#why-letsssl4windows)
- [Two editions, one release](#two-editions-one-release)
- [Features](#features)
- [Feature comparison](#feature-comparison)
- [Screenshots](#screenshots)
- [Quick start](#quick-start)
- [DNS-01 and wildcards](#dns-01-and-wildcards)
- [Deployment tasks](#deployment-tasks)
- [Notifications](#notifications)
- [Automatic renewal](#automatic-renewal)
- [Background service & system tray](#background-service--system-tray)
- [Appearance](#appearance)
- [Architecture](#architecture)
- [Build from source](#build-from-source)
- [Packaging & installer](#packaging--installer)
- [Development, tests & CI](#development-tests--ci)
- [Data & security](#data--security)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [Support the project](#support-the-project)
- [License](#license)

---

## Why LetsSSL4Windows?

Certify The Web is excellent, but its free edition is limited and the full feature
set needs a commercial license. **LetsSSL4Windows** delivers the same everyday
workflow — a friendly GUI for Windows/IIS admins that *just works* — under a
permissive MIT license you can use, fork, and ship without restriction. No
per-certificate caps, no paid tiers, no telemetry.

## Two editions, one release

LetsSSL4Windows comes in **two editions**, and **every GitHub Release contains
both** so you can pick whichever fits your workflow:

| Edition | What it is | Get it from a release |
| --- | --- | --- |
| **Desktop app (exe)** | A WPF dashboard + system tray + background renewal service, packaged as a Windows installer. | `LetsSSL4Windows-Setup-<version>.exe` |
| **PowerShell edition (ps1)** | A single, self-contained console script — same features, no install, automated via a Scheduled Task. | `LetsSSL4Windows-<version>.ps1` (and `LetsSSL4Windows-PowerShell-<version>.zip` with the module + tests) |

Both editions read and write the **same data store** (`%ProgramData%\LetsSSL4Windows`),
so they're interchangeable and can even coexist — the PowerShell edition can see,
renew, bind, and export certificates the desktop app created, and vice versa. See
[`powershell/README.md`](powershell/README.md) for the console edition's docs.

## Features

- 🔐 **Let's Encrypt issuance** over ACME (RFC 8555), powered by the MIT-licensed [Certes](https://github.com/fszlin/certes) client.
- 🌐 **HTTP-01 and DNS-01 validation**, including **wildcard certificates** (`*.example.com`).
- ☁️ **DNS providers**: Cloudflare (automated) and Manual, with a pluggable provider interface.
- 🪟 **Windows-native**: installs to the `LocalMachine` certificate store and **auto-binds to IIS** with SNI.
- 🔁 **Automatic renewal** via a background Windows Service.
- 🚀 **Deployment tasks**: export PFX, export PEM (`fullchain.pem` + `privkey.pem`), or run a post-issue script.
- 📤 **On-demand export**: export any issued certificate from the dashboard as a password-protected PFX or as PEM (certificate + key).
- 📣 **Notifications**: email (SMTP) and webhook alerts on issuance/renewal success or failure.
- 🔒 **Encrypted secrets**: DNS API tokens and SMTP passwords are stored with Windows DPAPI.
- 🖥️ **Clean WPF dashboard** with a runtime-switchable dark/light theme, live activity log, and a guided New Certificate wizard.
- 🧰 **Background Windows Service** + **system-tray companion** for hands-off, always-on renewal and quick control.
- 💻 **PowerShell edition**: a single self-contained `.ps1` with the same feature set as a console UI (interactive menu + scriptable commands), unattended renewal via a Scheduled Task, an importer for existing certificates, and a reusable module API. Shipped in every release alongside the installer.

## Feature comparison

| Capability | Certify The Web | LetsSSL4Windows |
| --- | :---: | :---: |
| Let's Encrypt issuance (ACME) | ✅ | ✅ |
| HTTP-01 validation | ✅ | ✅ |
| DNS-01 validation | ✅ | ✅ (Cloudflare, Manual) |
| Wildcard certificates | ✅ | ✅ |
| Multi-domain (SAN) certs | ✅ | ✅ |
| Install to Windows cert store | ✅ | ✅ |
| Auto-bind to IIS (SNI) | ✅ | ✅ |
| Automatic renewal | ✅ | ✅ |
| Deployment tasks | ✅ | ✅ (PFX/PEM, script) |
| Email / webhook notifications | ✅ | ✅ |
| Encrypted credential storage | ✅ | ✅ (DPAPI) |
| Dashboard GUI | ✅ | ✅ |
| **License** | Freemium / commercial | **MIT (free)** |

## Screenshots

> _Screenshots coming soon. To add yours, drop images in `docs/img/` and reference them here, e.g.:_
>
> `![Dashboard](docs/img/dashboard.png)`

## Quick start

```powershell
# 1. Clone
git clone https://github.com/MikeYEG/LetsSSL4Windows.git
cd LetsSSL4Windows

# 2. Build (requires the .NET 8 SDK)
dotnet build -c Release

# 3. Launch the GUI (you will be prompted for elevation)
dotnet run --project src/LetsSSL.App -c Release
```

Or open `LetsSSL.sln` in **Visual Studio 2022**, set **LetsSSL.App** as the startup
project, and press <kbd>F5</kbd>. (The GUI builds as `LetsSSL4Windows.exe`.)

**First run:**

1. Open **Settings** and keep the environment on **Staging** for your first try
   (Let's Encrypt staging has generous rate limits and issues *untrusted* test
   certificates — perfect for verifying the flow).
2. Click **+ New certificate**, pick an IIS site (the web root auto-fills),
   confirm the domain and contact email, and click **Request certificate**.
3. Watch the activity log. On success the certificate appears in the grid with a
   green status and an expiry date.
4. Once staging works end-to-end, switch Settings to **Production** for a real,
   browser-trusted certificate.

**Prefer the console?** Grab `LetsSSL4Windows-<version>.ps1` from the release (or
run it from `powershell/`) — no build or install required. See
[`powershell/README.md`](powershell/README.md) for the full console walkthrough.

## DNS-01 and wildcards

Choose **DNS-01** in the New Certificate wizard to validate via a TXT record —
required for wildcard names like `*.example.com`. Two providers ship today:

- **Cloudflare** — paste an API token with `Zone:DNS:Edit`. Records are created
  and cleaned up automatically, so wildcard certs renew unattended.
- **Manual** — the app shows the exact TXT record to create and waits for you to
  confirm (interactive only; the unattended renewer can't run manual DNS).

## Deployment tasks

Run post-issuance tasks (configured per certificate in the wizard or in
`certificates.json`):

- **Export PFX** to a path, optionally re-encrypted with your own password.
- **Export PEM** (`fullchain.pem` + `privkey.pem`) for nginx/Apache/HAProxy.
- **Run script** — runs a `.ps1`/exe with `LETSSSL4WINDOWS_THUMBPRINT`, `LETSSSL4WINDOWS_DOMAIN`,
  `LETSSSL4WINDOWS_PFX_PATH`, and `LETSSSL4WINDOWS_PFX_PASSWORD` in the environment.

## Notifications

Get alerted when certificates are issued/renewed — or when a renewal fails (the
case you most want to know about). Configure channels in **Settings → Notifications**:

- **Webhook** — a JSON payload (`{ text, status, domain, subject, body, timestamp }`)
  is POSTed to your URL, compatible with Slack/Teams/Discord incoming webhooks or a
  custom endpoint.
- **Email (SMTP)** — host, port, SSL, credentials, and from/to addresses. The SMTP
  password is stored encrypted with Windows DPAPI.

Choose whether to notify on success, on failure, or both. Notifications are
best-effort — a delivery failure is logged and never blocks issuance.

## Automatic renewal

Unattended renewal is handled by the **Windows Service** — see
[Background service & system tray](#background-service--system-tray) below for
installation. It checks every 12 hours and reissues any certificate within its
renewal window (default: 30 days before expiry). You can also trigger an immediate
renewal any time with **Renew all due** in the GUI or from the tray.

## Background service & system tray

Everything ships in **one executable**, `LetsSSL4Windows.exe`, which runs in
different modes depending on its argument:

| Command | Mode |
| --- | --- |
| `LetsSSL4Windows.exe` | Dashboard GUI (self-elevates for cert-store/IIS access) |
| `LetsSSL4Windows.exe --tray` | System-tray companion (runs un-elevated; can start at login) |
| `LetsSSL4Windows.exe --service` | Windows Service worker (started by the SCM) |
| `LetsSSL4Windows.exe --install-service` | Register + start the renewal service (elevated) |
| `LetsSSL4Windows.exe --uninstall-service` | Stop + remove the service (elevated) |

The installer handles all of this for you. To do it manually after publishing
(`build\publish\LetsSSL4Windows.exe`), from an elevated prompt:

```powershell
& "build\publish\LetsSSL4Windows.exe" --install-service   # creates + starts the service
& "build\publish\LetsSSL4Windows.exe" --uninstall-service # removes it
```

The service registers as **"LetsSSL4Windows Renewal Service"** in services.msc and
checks for due renewals every 12 hours. From the tray icon you can open the
dashboard, **Renew all due now**, start/stop the renewal service, toggle **Start
with Windows**, and see the next expiry in the tooltip.

## Appearance

The app ships in **dark mode** by default. Switch themes at runtime with the
**Toggle theme** button in the header, or in **Settings → Appearance → Dark mode**.
Your choice is saved and applied on the next launch, and the Windows title bar
follows the theme.

## Architecture

All components share one data store under `%ProgramData%\LetsSSL4Windows`.
Internal C# namespaces stay `LetsSSL.*`; the built assemblies are branded
`LetsSSL4Windows.*`.

```
src/
  LetsSSL.Core/         # ACME, DNS providers, IIS, cert store, deployment, renewal (net8.0)
  LetsSSL.App/          # The single app exe: GUI + tray + service (net8.0-windows)
tests/
  LetsSSL.Core.Tests/   # xUnit tests for the core logic
powershell/
  LetsSSL4Windows.ps1   # The self-contained PowerShell edition (console UI + commands)
  LetsSSL4Windows.psm1  # Module wrapper that exports the reusable functions
  LetsSSL4Windows.psd1  # Module manifest
  tests/                # Pester v5 tests
```

- **LetsSSL.Core** — all logic, no UI dependencies. Wraps Certes, publishes
  HTTP-01/DNS-01 challenges, imports PFXs into `LocalMachine\My`, manages IIS
  bindings via `Microsoft.Web.Administration`, runs deployment tasks, and persists
  state as JSON.
- **LetsSSL.App** — one multi-mode executable (`LetsSSL4Windows.exe`): the WPF
  dashboard, the WinForms system tray, and the Windows Service worker, selected by
  command-line argument (see [above](#background-service--system-tray)).
- **powershell/** — the standalone PowerShell edition. It reimplements the same
  workflow on top of [Posh-ACME](https://github.com/rmbolger/Posh-ACME) and the
  built-in `WebAdministration`/cert-store cmdlets, and uses the **same data store**
  so it interoperates with the desktop app. See [`powershell/README.md`](powershell/README.md).

## Build from source

### Prerequisites

- Windows 10/11 or Windows Server 2016+
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- For IIS binding: IIS installed; run the app **as Administrator** (it ships with
  a manifest requesting elevation, required to write the machine cert store and
  IIS configuration).
- For HTTP-01: the domain must resolve to this server and be reachable on port 80.

```powershell
dotnet restore
dotnet build -c Release
```

## Packaging & installer

LetsSSL4Windows ships as a single Windows installer (`LetsSSL4Windows-Setup-<version>.exe`)
built with [Inno Setup](https://jrsoftware.org/isdl.php). It bundles the GUI,
the renewal service, the tray companion, and the agent, then:

- installs everything into `C:\Program Files\LetsSSL4Windows`,
- creates Start Menu (and optional desktop) shortcuts,
- optionally installs and starts the **renewal service**,
- optionally starts the **tray** at login,
- and on uninstall, stops and removes the service first.

### Build the installer locally

Prerequisites: the **.NET 8 SDK** and **Inno Setup 6**. From the repo root in
PowerShell:

```powershell
.\build\build-installer.ps1 -Version 1.0.0
# smaller build that needs the .NET 8 Desktop Runtime on the target machine:
.\build\build-installer.ps1 -Version 1.0.0 -FrameworkDependent
```

The installer is written to `build\installer-output\`. By default the published
apps are **self-contained** (win-x64), so end users don't need the .NET runtime.
To just publish the executables without building an installer, run
`.\build\publish.ps1` (output in `build\publish\`).

### What's in a release

Each release is a **single GitHub Release** that bundles **both editions**:

- **`LetsSSL4Windows-Setup-<version>.exe`** — the desktop app installer (exe edition).
- **`LetsSSL4Windows-<version>.ps1`** — the standalone PowerShell script (ps1 edition).
- **`LetsSSL4Windows-PowerShell-<version>.zip`** — the full PowerShell edition
  (script + module + README + Pester tests).

The PowerShell edition needs no build step — it's published straight from
`powershell/`.

### Automated releases

Pushing a version tag builds and publishes everything automatically via
[`.github/workflows/release.yml`](.github/workflows/release.yml) (Inno Setup is
installed on the runner). It compiles and packages the installer **and** packages
the PowerShell edition, then attaches all of them to the **same** GitHub Release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## Development, tests & CI

Unit tests for the pure Core logic live in `tests/LetsSSL.Core.Tests` (xUnit):

```powershell
dotnet test
```

The PowerShell edition has its own [Pester](https://pester.dev) v5 tests in
`powershell/tests/` (data model, status/renewal logic, JSON store, settings,
selectors, DPAPI round-trip):

```powershell
Invoke-Pester -Path .\powershell\tests\LetsSSL4Windows.Tests.ps1
```

Continuous integration is configured in [`.github/workflows/ci.yml`](.github/workflows/ci.yml):
every push and pull request to `main` restores, builds the full solution, and runs
the tests on `windows-latest` (Windows is required because the GUI targets WPF).

To initialize the repository and make the first commit, run the helper script
from the repository root:

```powershell
.\scripts\init-repo.ps1
# optionally push to a remote:
.\scripts\init-repo.ps1 -RemoteUrl https://github.com/MikeYEG/LetsSSL4Windows.git
```

## Data & security

- State lives in `%ProgramData%\LetsSSL4Windows`: `appsettings.json`,
  `certificates.json`, the ACME account key (`accounts/`), saved PFXs (`pfx/`),
  and logs.
- DNS API tokens and SMTP passwords are encrypted with Windows DPAPI (LocalMachine
  scope) so the SYSTEM renewal service can also read them.
- Private keys are imported into the machine store as exportable; restrict access
  to the `%ProgramData%\LetsSSL4Windows` folder accordingly.

## Roadmap

- [x] Background Windows service with a system-tray companion
- [x] Light/dark theme toggle
- [ ] More DNS providers (Route 53, Azure DNS, Google Cloud DNS)
- [ ] Additional ACME CAs (e.g. ZeroSSL, Buypass)
- [ ] More deployment task types

## Contributing

Contributions are very welcome! Please:

1. Open an issue to discuss substantial changes first.
2. Fork the repo and create a feature branch.
3. Keep `dotnet build` and `dotnet test` green (CI enforces this).
4. Open a pull request describing the change.

## Support the project

LetsSSL4Windows is free and always will be. If it helps you, you can support
ongoing development:

- ❤️ [**Sponsor on GitHub**](https://github.com/sponsors/MikeYEG)
- ☕ [**Buy Me a Coffee**](https://www.buymeacoffee.com/MikeYEG)
- ⭐ Star the repo and share it with other Windows admins!

## License

[MIT](LICENSE) © MikeYEG and contributors. Third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); all are permissively licensed.
Let's Encrypt is a service of the Internet Security Research Group (ISRG);
certificates are subject to Let's Encrypt's Subscriber Agreement and rate limits.
