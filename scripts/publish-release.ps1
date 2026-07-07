param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = ""
)

<#
.SYNOPSIS
    Builds and packages LocalOpsBot for release.
.DESCRIPTION
    - Publishes Agent and Tray as self-contained apps
    - Creates release/LocalOpsBot.Agent.zip and release/LocalOpsBot.Tray.zip
    - Copies install.ps1, uninstall.ps1, and appsettings.example.json to release/
.DESCRIPTION
    Run from the repository root.
#>

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot "release" }

$PublishAgent = Join-Path $RepoRoot "publish\Agent"
$PublishTray = Join-Path $RepoRoot "publish\Tray"
$AgentZip = Join-Path $OutputDir "LocalOpsBot.Agent.zip"
$TrayZip = Join-Path $OutputDir "LocalOpsBot.Tray.zip"

# Ensure output directory
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "=== Publishing Agent (self-contained) ==="
dotnet publish $RepoRoot\src\LocalOpsBot.Agent\LocalOpsBot.Agent.csproj `
    -c $Configuration -r $Runtime --self-contained true -o $PublishAgent

if ($LASTEXITCODE -ne 0) {
    Write-Error "Agent publish failed"
    exit 1
}

Write-Host "`n=== Publishing Tray (self-contained) ==="
dotnet publish $RepoRoot\src\LocalOpsBot.Tray\LocalOpsBot.Tray.csproj `
    -c $Configuration -r $Runtime --self-contained true -o $PublishTray

if ($LASTEXITCODE -ne 0) {
    Write-Error "Tray publish failed"
    exit 1
}

Write-Host "`n=== Creating zip archives ==="
Compress-Archive -Path "$PublishAgent\*" -DestinationPath $AgentZip -Force
Write-Host "Created: $AgentZip"
$agentHash = (Get-FileHash $AgentZip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$AgentZip.sha256" -Value $agentHash
Write-Host "SHA256: $agentHash"

Compress-Archive -Path "$PublishTray\*" -DestinationPath $TrayZip -Force
Write-Host "Created: $TrayZip"
$trayHash = (Get-FileHash $TrayZip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$TrayZip.sha256" -Value $trayHash
Write-Host "SHA256: $trayHash"

Write-Host "`n=== Copying installer scripts and config ==="
Copy-Item (Join-Path $RepoRoot "installer\install-service.ps1") (Join-Path $OutputDir "install.ps1") -Force
Copy-Item (Join-Path $RepoRoot "installer\uninstall-service.ps1") (Join-Path $OutputDir "uninstall.ps1") -Force
Copy-Item (Join-Path $RepoRoot "installer\register-startup-tray.ps1") (Join-Path $OutputDir "register-startup-tray.ps1") -Force

$configSource = Join-Path $RepoRoot "config\appsettings.example.json"
if (Test-Path $configSource) {
    Copy-Item $configSource (Join-Path $OutputDir "appsettings.example.json") -Force
} else {
    $schemaSource = Join-Path $RepoRoot "schemas\appsettings.sample.json"
    if (Test-Path $schemaSource) {
        Copy-Item $schemaSource (Join-Path $OutputDir "appsettings.example.json") -Force
    }
}

Write-Host "`n=== Release package created in: $OutputDir ==="
Get-ChildItem $OutputDir | Select-Object Name, Length
