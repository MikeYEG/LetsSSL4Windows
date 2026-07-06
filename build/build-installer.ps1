<#
.SYNOPSIS
  Publishes LetsSSL4Windows and compiles the Inno Setup installer.
.DESCRIPTION
  Runs build\publish.ps1, then invokes the Inno Setup compiler (ISCC.exe) on
  installer\LetsSSL4Windows.iss. The resulting installer is written to
  build\installer-output\.

  Prerequisites:
    - .NET 8 SDK
    - Inno Setup 6 (https://jrsoftware.org/isdl.php)
.EXAMPLE
  .\build\build-installer.ps1 -Version 1.0.0
.EXAMPLE
  .\build\build-installer.ps1 -Version 1.0.0 -FrameworkDependent
#>
param(
    [string]$Version = "1.0.0",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# 1) Publish the executable (stamping the assembly version to match the release).
& (Join-Path $PSScriptRoot "publish.ps1") -Version $Version -FrameworkDependent:$FrameworkDependent

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
