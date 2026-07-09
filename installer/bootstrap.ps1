<#
.SYNOPSIS
    Bootstrap installer for Homebase — downloads and runs setup.
.DESCRIPTION
    Run this one-liner from an elevated PowerShell prompt:
      irm https://github.com/jeiel85/homebase/releases/latest/download/bootstrap.ps1 | iex
    Downloads the latest release, extracts everything, and launches interactive setup.
#>

#Requires -RunAsAdministrator

$Repo = "jeiel85/homebase"
$TempDir = Join-Path $env:TEMP "LocalOpsBot-Bootstrap"
$SetupZip = Join-Path $TempDir "Homebase-Setup.zip"

Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "  Homebase — Bootstrap Installer" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host ""

# --- Clean temp ---
if (Test-Path $TempDir) { Remove-Item $TempDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null

# --- Fetch latest release ---
Write-Host ">>> Detecting latest release..." -ForegroundColor Cyan
try {
    $api = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing
    $tag = $api.tag_name
    Write-Host "  Latest: $tag" -ForegroundColor Green
} catch {
    Write-Warning "GitHub API failed, falling back to 'latest' tag."
    $tag = "latest"
}

# --- Download Setup.zip ---
$url = "https://github.com/$Repo/releases/$tag/download/Homebase-Setup.zip"
Write-Host ">>> Downloading Setup.zip..." -ForegroundColor Cyan
try {
    Invoke-WebRequest -Uri $url -OutFile $SetupZip -UseBasicParsing
} catch {
    Write-Host "  Retrying with /latest/download..." -ForegroundColor Yellow
    $url = "https://github.com/$Repo/releases/latest/download/Homebase-Setup.zip"
    Invoke-WebRequest -Uri $url -OutFile $SetupZip -UseBasicParsing
}
Write-Host "  Downloaded to $SetupZip" -ForegroundColor Green

# --- Extract ---
Write-Host ">>> Extracting..." -ForegroundColor Cyan
Expand-Archive -Path $SetupZip -DestinationPath $TempDir -Force

# setup.ps1 is at the zip root (no wrapper folder)
$SetupScript = Join-Path $TempDir "setup.ps1"
if (Test-Path $SetupScript) {
    Write-Host ">>> Launching setup..." -ForegroundColor Cyan
    & $SetupScript @args
} else {
    Write-Error "setup.ps1 not found in extracted archive"
    exit 1
}
