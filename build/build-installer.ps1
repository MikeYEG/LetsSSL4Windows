<#
.SYNOPSIS
  Publishes LetsSSL4Windows and compiles the Inno Setup installer.
.DESCRIPTION
  Runs build\publish.ps1, then invokes the Inno Setup compiler (ISCC.exe) on
  installer\LetsSSL4Windows.iss. The resulting installer is written to
  build\installer-output\.

  The installer ships the framework-dependent build (a few MB rather than the
  ~150 MB self-contained exe) and installs the .NET 8 Desktop Runtime on the
  target machine if it isn't already present. For the self-contained portable
  exe (no runtime prerequisite), run build\publish.ps1 directly.

  Prerequisites:
    - .NET 8 SDK
    - Inno Setup 6 (https://jrsoftware.org/isdl.php)
.EXAMPLE
  .\build\build-installer.ps1 -Version 1.0.0
#>
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# 1) Publish the framework-dependent build the installer packages, into the
#    folder installer\LetsSSL4Windows.iss sources from.
& (Join-Path $PSScriptRoot "publish.ps1") -Version $Version -FrameworkDependent -OutputDir "build\publish-fd"

# 2) Locate the Inno Setup compiler.
$isccCmd = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
$iscc = if ($isccCmd) { $isccCmd.Source } else { $null }
if (-not $iscc) {
    foreach ($candidate in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if ($candidate -and (Test-Path $candidate)) { $iscc = $candidate; break }
    }
}
if (-not $iscc) {
    throw "Inno Setup compiler (ISCC.exe) not found. Install Inno Setup 6 from https://jrsoftware.org/isdl.php"
}

# 3) Compile the installer.
$iss = Join-Path $root "installer\LetsSSL4Windows.iss"
Write-Host "Compiling installer (v$Version) with $iscc ..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed." }

$outDir = Join-Path $root "build\installer-output"
Write-Host "`nInstaller written to $outDir" -ForegroundColor Green
Get-ChildItem $outDir -Filter *.exe | Select-Object Name, Length | Format-Table
