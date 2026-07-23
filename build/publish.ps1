<#
.SYNOPSIS
  Publishes the single LetsSSL4Windows executable into build\publish.
.DESCRIPTION
  Publishes the unified app (GUI + service + tray in one exe) for win-x64 as a
  single self-contained file (no .NET runtime required on the target machine).
  Pass -FrameworkDependent for a much smaller output that requires the .NET 8
  Desktop Runtime to be installed.
.EXAMPLE
  .\build\publish.ps1
.EXAMPLE
  .\build\publish.ps1 -FrameworkDependent
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0",
    [switch]$FrameworkDependent,
    # Where the published .exe lands. Relative paths resolve against the repo
    # root. The release build publishes twice: the self-contained portable exe
    # into the default folder, and a framework-dependent build (for the
    # installer) into a separate one.
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$buildRoot = Join-Path $root "build"

# Resolve the output folder. Clear-OutputDir below wipes this folder's contents,
# so constrain it to $root\build — a stray -OutputDir like '..\..' must never let
# the publish delete something outside the repo's build area.
$out = if ($OutputDir) {
    if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $root $OutputDir }
} else {
    Join-Path $buildRoot "publish"
}
$outFull = [System.IO.Path]::GetFullPath($out)
$buildRootFull = [System.IO.Path]::GetFullPath($buildRoot)
if ($outFull -ne $buildRootFull -and
    -not $outFull.StartsWith($buildRootFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDir must resolve to a folder under '$buildRootFull' (got '$outFull')."
}
$out = $outFull

# A running LetsSSL4Windows.exe launched from the output folder locks its files.
# Stop only the instances running from $out (leaves the installed app/service alone).
Get-Process -Name "LetsSSL4Windows" -ErrorAction SilentlyContinue | ForEach-Object {
    $path = $null
    try { $path = $_.Path } catch { }
    if ($path -and $path.StartsWith($out, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Stopping running $($_.ProcessName) (PID $($_.Id)) from the publish folder..." -ForegroundColor Yellow
        try { $_ | Stop-Process -Force -ErrorAction Stop } catch { }
    }
}

# Clear the folder's CONTENTS (not the folder itself) so an open Explorer window
# on build\publish doesn't block it. Retry for a while: the usual culprit is
# Windows Defender still scanning the previous (large) exe, which clears in a few
# seconds. Also try renaming a stubborn leftover exe aside as a last resort.
function Clear-OutputDir([string]$path) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
        return
    }
    for ($i = 1; $i -le 12; $i++) {
        try {
            Get-ChildItem -LiteralPath $path -Recurse -Force -ErrorAction Stop |
                Remove-Item -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($i -eq 12) {
                # Last resort: move any locked leftover exe out of the way so the
                # publish can still write a fresh one.
                Get-ChildItem -LiteralPath $path -Filter *.exe -ErrorAction SilentlyContinue | ForEach-Object {
                    try { Rename-Item $_.FullName "$($_.Name).old-$([guid]::NewGuid().ToString('N').Substring(0,6))" -ErrorAction Stop }
                    catch {
                        throw "Could not clear '$path' - a file is locked. This is usually Windows Defender scanning the last build or an open Explorer window on that folder. Wait ~10s, close any Explorer window there, and run this again.`n$($_.Exception.Message)"
                    }
                }
                return
            }
            Start-Sleep -Seconds 1
        }
    }
}

Clear-OutputDir $out

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

# Single-file settings live in the .csproj so the output is always one .exe.
$proj = Join-Path $root "src\LetsSSL.App\LetsSSL.App.csproj"
Write-Host "Publishing LetsSSL4Windows.exe (self-contained=$selfContained) ..." -ForegroundColor Cyan
dotnet publish $proj `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContained `
    -p:Version=$Version `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# Rename the output to include the version, e.g. LetsSSL4Windows-1.2.3.exe. The
# installer sources this file but installs it under the canonical name, so the
# app/service/shortcuts are unaffected; only the portable file carries the version.
$builtExe = Join-Path $out "LetsSSL4Windows.exe"
$versionedExe = Join-Path $out "LetsSSL4Windows-$Version.exe"
if (Test-Path $builtExe) {
    if (Test-Path $versionedExe) { Remove-Item $versionedExe -Force }
    Rename-Item $builtExe $versionedExe
}

Write-Host "`nPublished to $out" -ForegroundColor Green
Get-ChildItem $out -Filter *.exe | Select-Object Name, Length | Format-Table
