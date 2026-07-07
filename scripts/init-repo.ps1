<#
.SYNOPSIS
  Initializes the LetsSSL4Windows git repository and makes the first commit.
.DESCRIPTION
  Run once from the repository root in PowerShell. Optionally pass a remote URL
  to add an 'origin' and push.
.EXAMPLE
  .\scripts\init-repo.ps1
.EXAMPLE
  .\scripts\init-repo.ps1 -RemoteUrl https://github.com/MikeYEG/LetsSSL4Windows.git
#>
param(
    [string]$RemoteUrl
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (Test-Path .git) {
    Write-Host 'A git repository already exists here.' -ForegroundColor Yellow
} else {
    git init -b main
}

git add .
git commit -m "Initial commit: LetsSSL4Windows - open-source Windows ACME/SSL manager"

if ($RemoteUrl) {
    git remote add origin $RemoteUrl
    git push -u origin main
    Write-Host "Pushed to $RemoteUrl" -ForegroundColor Green
}

Write-Host 'Done.' -ForegroundColor Green
