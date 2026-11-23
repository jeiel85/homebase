<#
.SYNOPSIS
    Bootstrap installer for Homebase — downloads and runs setup.
.DESCRIPTION
    Run this one-liner from an elevated PowerShell prompt:
      irm https://github.com/jeiel85/homebase/releases/latest/download/bootstrap.ps1 | iex
    Downloads the latest release, extracts everything, and launches interactive setup.
    Setup asks for a Telegram bot token only as an optional fallback channel: press Enter to skip
    it (the Homebase phone app is the main channel) and add it later from the tray if wanted.
#>

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"
$Repo = "jeiel85/homebase"
$TempDir = Join-Path $env:ProgramData ("Homebase-Bootstrap-" + [Guid]::NewGuid().ToString("N"))
$SetupZip = Join-Path $TempDir "Homebase-Setup.zip"
$ExtractDir = Join-Path $TempDir "package"

# Create a SYSTEM + Administrators-only work folder in one step so non-elevated processes cannot
# tamper with the downloaded archive or extracted setup.ps1 before execution.
function New-PrivateDirectory($Path) {
    if (Test-Path -LiteralPath $Path) { throw "Work folder already exists: $Path" }
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier($sid)), 'FullControl', $inherit, 'None', 'Allow')))
    }
    [System.IO.Directory]::CreateDirectory($Path, $acl) | Out-Null
}

Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "  Homebase — Bootstrap Installer" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host ""

try {
    New-PrivateDirectory $TempDir

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

    # --- Download Setup.zip and its SHA-256 checksum ---
    $baseUrl = if ($tag -eq "latest") { "https://github.com/$Repo/releases/latest/download" } else { "https://github.com/$Repo/releases/download/$tag" }
    $zipUrl = "$baseUrl/Homebase-Setup.zip"
    $shaUrl = "$baseUrl/Homebase-Setup.zip.sha256"

    Write-Host ">>> Downloading Setup.zip..." -ForegroundColor Cyan
    try {
        Invoke-WebRequest -Uri $zipUrl -OutFile $SetupZip -UseBasicParsing
        $rawSha = (Invoke-WebRequest -Uri $shaUrl -UseBasicParsing).Content
    } catch {
        Write-Host "  Retrying with /latest/download..." -ForegroundColor Yellow
        $baseUrl = "https://github.com/$Repo/releases/latest/download"
        Invoke-WebRequest -Uri "$baseUrl/Homebase-Setup.zip" -OutFile $SetupZip -UseBasicParsing
        $rawSha = (Invoke-WebRequest -Uri "$baseUrl/Homebase-Setup.zip.sha256" -UseBasicParsing).Content
    }
    Write-Host "  Downloaded to $SetupZip" -ForegroundColor Green

    # --- Verify SHA-256 checksum (fail closed) ---
    Write-Host ">>> Verifying SHA-256 checksum..." -ForegroundColor Cyan
    $expectedSha = (($rawSha -split '\s+') | Where-Object { $_ } | Select-Object -First 1)
    if (-not $expectedSha -or $expectedSha -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Published SHA-256 checksum is missing or invalid; aborting installation."
    }
    $actualSha = (Get-FileHash -LiteralPath $SetupZip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha -ne $expectedSha.ToLowerInvariant()) {
        throw "Checksum mismatch for Homebase-Setup.zip (expected $expectedSha, got $actualSha); aborting installation."
    }
    Write-Host "  SHA-256 verified ($actualSha)" -ForegroundColor Green

    # --- Extract ---
    Write-Host ">>> Extracting..." -ForegroundColor Cyan
    Expand-Archive -LiteralPath $SetupZip -DestinationPath $ExtractDir -Force

    # setup.ps1 is at the zip root (no wrapper folder)
    $SetupScript = Join-Path $ExtractDir "setup.ps1"
    if (Test-Path -LiteralPath $SetupScript) {
        Write-Host ">>> Launching setup..." -ForegroundColor Cyan
        & $SetupScript @args
    } else {
        Write-Error "setup.ps1 not found in extracted archive"
        exit 1
    }
} finally {
    if (Test-Path -LiteralPath $TempDir) {
        Remove-Item -LiteralPath $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
