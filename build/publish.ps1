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
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "build\publish"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

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

Write-Host "`nPublished to $out" -ForegroundColor Green
Get-ChildItem $out -Filter *.exe | Select-Object Name, Length | Format-Table
