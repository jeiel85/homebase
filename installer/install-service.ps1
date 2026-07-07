param(
    [string]$AgentSource = "",
    [string]$TraySource = ""
)

<#
.SYNOPSIS
    Installs LocalOpsBot Agent as a Windows service and registers Tray for auto-start.
.DESCRIPTION
    - Copies binaries from source directories to Program Files
    - Creates config/data/log directories in ProgramData
    - Creates Windows service with automatic recovery
    - Registers Tray app in HKCU startup
    - Starts the service

    Run from an elevated PowerShell prompt.
.PARAMETER AgentSource
    Path to pre-built Agent binaries. Default: ..\publish\Agent
.PARAMETER TraySource
    Path to pre-built Tray binaries. Default: ..\publish\Tray
#>

#Requires -RunAsAdministrator

$ServiceName = "LocalOpsBot.Agent"
$DisplayName = "LocalOpsBot Agent"
$AgentDir = "C:\Program Files\LocalOpsBot\Agent"
$TrayDir = "C:\Program Files\LocalOpsBot\Tray"
$AgentExe = Join-Path $AgentDir "LocalOpsBot.Agent.exe"
$TrayExe = Join-Path $TrayDir "LocalOpsBot.Tray.exe"
$ConfigDir = "C:\ProgramData\LocalOpsBot\config"
$DataDir = "C:\ProgramData\LocalOpsBot\data"
$LogDir = "C:\ProgramData\LocalOpsBot\logs"

if (-not $AgentSource) { $AgentSource = Join-Path $PSScriptRoot "..\publish\Agent" }
if (-not $TraySource) { $TraySource = Join-Path $PSScriptRoot "..\publish\Tray" }
$AgentSource = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AgentSource)
$TraySource = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TraySource)

# --- Pre-flight checks ---
if (-not (Test-Path $AgentSource)) {
    Write-Error "Agent source not found: $AgentSource`nBuild first: dotnet publish src/LocalOpsBot.Agent -c Release -r win-x64 --self-contained true -o publish/Agent"
    exit 1
}
if (-not (Test-Path (Join-Path $AgentSource "LocalOpsBot.Agent.exe"))) {
    Write-Error "LocalOpsBot.Agent.exe not found in $AgentSource"
    exit 1
}

# --- Create directories ---
Write-Host "Creating directories..."
New-Item -ItemType Directory -Force -Path $AgentDir | Out-Null
New-Item -ItemType Directory -Force -Path $TrayDir | Out-Null
New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# --- Copy binaries ---
Write-Host "Copying Agent binaries to $AgentDir..."
Copy-Item "$AgentSource\*" $AgentDir -Recurse -Force

if (Test-Path $TraySource) {
    Write-Host "Copying Tray binaries to $TrayDir..."
    Copy-Item "$TraySource\*" $TrayDir -Recurse -Force
}

# --- Copy example config ---
$sampleConfig = Join-Path $PSScriptRoot "..\config\appsettings.example.json"
$exampleConfig = Join-Path $PSScriptRoot "..\schemas\appsettings.sample.json"
$configTarget = Join-Path $ConfigDir "appsettings.example.json"
if (Test-Path $sampleConfig) {
    Copy-Item $sampleConfig $configTarget -Force
    Write-Host "Example config copied to $configTarget"
} elseif (Test-Path $exampleConfig) {
    Copy-Item $exampleConfig $configTarget -Force
    Write-Host "Example config copied to $configTarget"
}

# --- Create/update Windows service ---
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service already exists. Stopping and recreating..."
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Creating service '$ServiceName'..."
sc.exe create $ServiceName binPath= "`"$AgentExe`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
sc.exe description $ServiceName "Personal Windows PC monitoring bot for Telegram." | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

Start-Service $ServiceName
Write-Host "Service started." -ForegroundColor Green

# --- Register Tray auto-start ---
if (Test-Path $TrayExe) {
    $existingPath = (Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LocalOpsBot.Tray" -ErrorAction SilentlyContinue)."LocalOpsBot.Tray"
    if (-not $existingPath) {
        New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LocalOpsBot.Tray" -Value "`"$TrayExe`"" -PropertyType String -Force | Out-Null
        Write-Host "Tray auto-start registered for current user." -ForegroundColor Green
    }
}

# --- Summary ---
Write-Host "`n=== LocalOpsBot Installation Complete ===" -ForegroundColor Green
Write-Host "Agent: $AgentDir"
Write-Host "Tray:  $TrayDir"
Write-Host "Config: $ConfigDir"
Write-Host "Data:  $DataDir"
Write-Host "Logs:  $LogDir"
Write-Host "`nNext steps:"
Write-Host "  1. Set your Telegram bot token:"
Write-Host "     [Environment]::SetEnvironmentVariable('LOCALOPSBOT_TELEGRAM_TOKEN', 'YOUR_TOKEN', 'Machine')"
Write-Host "  2. Edit config: $ConfigDir\appsettings.json"
Write-Host "     - Set your chat_id in allowedChatIds"
Write-Host "  3. Restart service: Restart-Service $ServiceName"
Write-Host "  4. Send /ping to your bot to test"
Write-Host ""
Get-Service $ServiceName
