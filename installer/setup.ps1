<#
.SYNOPSIS
    Installs LocalOpsBot Agent + Tray with interactive configuration.
.DESCRIPTION
    One-command installer that handles everything:
      - Prompts for Telegram bot token and allowed chat IDs
      - Copies binaries to Program Files
      - Creates config/data/log directories
      - Creates Windows service with auto-recovery
      - Registers Tray for auto-start
      - Sets machine-level environment variable for the token

    Run as Administrator.
.PARAMETER Token
    Telegram bot token (skips prompt).
.PARAMETER ChatId
    Allowed Telegram chat ID (skips prompt).
.PARAMETER NoInteractive
    Do not prompt — use defaults/環境変数 where possible.
.PARAMETER AgentSource
    Path to Agent binaries (auto-detected from script location).
.PARAMETER TraySource
    Path to Tray binaries (auto-detected from script location).
.EXAMPLE
    .\setup.ps1
    Fully interactive install.
.EXAMPLE
    .\setup.ps1 -Token "123:ABC" -ChatId "98765"
    Unattended install with known values.
.EXAMPLE
    # One-liner from web:
    irm https://github.com/jeiel85/localops-bot/releases/latest/download/bootstrap.ps1 | iex
#>

#Requires -RunAsAdministrator
param(
    [string]$Token = "",
    [string]$ChatId = "",
    [switch]$NoInteractive,
    [string]$AgentSource = "",
    [string]$TraySource = ""
)

# --- Constants ---
$ServiceName  = "LocalOpsBot.Agent"
$DisplayName  = "LocalOpsBot Agent"
$AgentDir     = "C:\Program Files\LocalOpsBot\Agent"
$TrayDir      = "C:\Program Files\LocalOpsBot\Tray"
$AgentExe     = Join-Path $AgentDir "LocalOpsBot.Agent.exe"
$TrayExe      = Join-Path $TrayDir "LocalOpsBot.Tray.exe"
$ConfigDir    = "C:\ProgramData\LocalOpsBot\config"
$DataDir      = "C:\ProgramData\LocalOpsBot\data"
$LogDir       = "C:\ProgramData\LocalOpsBot\logs"
$ProgramData  = "C:\ProgramData\LocalOpsBot"
$EnvVarName   = "LOCALOPSBOT_TELEGRAM_TOKEN"
$ConfigFile   = Join-Path $ConfigDir "appsettings.json"
$ConfigSample = Join-Path $ConfigDir "appsettings.example.json"
$GitHubRepo   = "jeiel85/localops-bot"

# --- Detect source paths ---
$ScriptRoot = Split-Path -Parent $PSCommandPath
if (-not $AgentSource) {
    $candidate = Join-Path $ScriptRoot "..\Agent"
    if (Test-Path $candidate) { $AgentSource = Resolve-Path $candidate }
}
if (-not $AgentSource -or -not (Test-Path (Join-Path $AgentSource "LocalOpsBot.Agent.exe"))) {
    $candidate = Join-Path $ScriptRoot "Agent"
    if (Test-Path $candidate) { $AgentSource = Resolve-Path $candidate }
}
if (-not $TraySource) {
    $candidate = Join-Path $ScriptRoot "..\Tray"
    if (Test-Path $candidate) { $TraySource = Resolve-Path $candidate }
}
if (-not $TraySource -or -not (Test-Path (Join-Path $TraySource "LocalOpsBot.Tray.exe"))) {
    $candidate = Join-Path $ScriptRoot "Tray"
    if (Test-Path $candidate) { $TraySource = Resolve-Path $candidate }
}

$HasAgentBinaries = $AgentSource -and (Test-Path (Join-Path $AgentSource "LocalOpsBot.Agent.exe"))
$HasTrayBinaries  = $TraySource  -and (Test-Path (Join-Path $TraySource  "LocalOpsBot.Tray.exe"))

# --- Detect config sample ---
$SampleCandidates = @(
    Join-Path $ScriptRoot "..\config\appsettings.example.json"
    Join-Path $ScriptRoot "config\appsettings.example.json"
    Join-Path $ScriptRoot "appsettings.example.json"
)
$ConfigSampleSrc = $null
foreach ($c in $SampleCandidates) {
    $abs = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($c)
    if (Test-Path $abs) { $ConfigSampleSrc = $abs; break }
}

# ============================================================
#  FUNCTIONS
# ============================================================

function Write-Step($Msg) {
    Write-Host ">>> $Msg" -ForegroundColor Cyan
}

function Write-Ok($Msg) {
    Write-Host "  [$([char]0x2713)] $Msg" -ForegroundColor Green
}

function Write-Warn($Msg) {
    Write-Host "  [!] $Msg" -ForegroundColor Yellow
}

function Write-ErrorExit($Msg) {
    Write-Host "  [X] $Msg" -ForegroundColor Red
    exit 1
}

function Test-BotToken($t) {
    return $t -match '^\d{6,10}:[A-Za-z0-9_-]{30,}$'
}

function Test-ChatId($c) {
    return $c -match '^-?\d{4,}$'
}

function Read-Token {
    $existing = [Environment]::GetEnvironmentVariable($EnvVarName, 'Machine')
    if ($existing) {
        $masked = $existing.Substring(0, [Math]::Min(8, $existing.Length)) + '...'
        $ans = Read-Host "Bot token already set [$masked]. Change? (y/N)"
        if ($ans -ne 'y' -and $ans -ne 'Y') { return $existing }
    }
    do {
        $t = Read-Host "`nEnter your Telegram bot token (from @BotFather)"
        if (-not $t) { continue }
        if (Test-BotToken $t) { return $t }
        Write-Warn "Invalid format. Should look like: 123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11"
    } while ($true)
}

function Read-ChatId {
    do {
        $c = Read-Host "`nEnter your Telegram chat ID (numeric, e.g. 123456789)"
        if (-not $c) { continue }
        if (Test-ChatId $c) { return $c }
        Write-Warn "Invalid. Send any message to your bot, then visit:"
        Write-Warn "  https://api.telegram.org/bot$Token/getUpdates"
        Write-Warn "  Look for `"chat`.`"id`" value."
    } while ($true)
}

function Write-Config {
    param($Token, $ChatId)
    $config = @"
{
  "telegram": {
    "token": "",
    "allowedChatIds": [$ChatId],
    "sendBootNotification": true,
    "botDisplayName": "LocalOps Bot"
  },
  "monitoring": {
    "intervalSeconds": 30,
    "disk": {
      "warnFreeBelowGb": 20,
      "checkIntervalSeconds": 300
    },
    "processWatches": ["ollama"],
    "serviceWatches": ["PostgreSQL"]
  },
  "eventLog": {
    "enabled": true,
    "level": "Warning",
    "maxEvents": 50,
    "watchIntervalSeconds": 60,
    "watchedLogs": ["System", "Application"]
  },
  "notificationForwarding": {
    "enabled": false,
    "historyCount": 50,
    "filterPattern": "",
    "maskPatterns": ["(?i)(password|secret|token)"]
  },
  "alerting": {
    "enabled": true,
    "dedupWindowSeconds": 300,
    "rateLimitPerMinute": 5,
    "muteCommands": ["mute", "unmute"]
  },
  "developer": {
    "httpEndpoints": [],
    "tcpPorts": []
  },
  "update": {
    "checkOnStartup": true,
    "checkIntervalHours": 24,
    "channel": "stable"
  },
  "data": {
    "connectionString": "Data Source=$DataDir\localops.db"
  }
}
"@
    $config | Set-Content -Path $ConfigFile -Encoding UTF8
    Write-Ok "Configuration written to $ConfigFile"
}

# ============================================================
#  MAIN
# ============================================================

Write-Host "`n==============================================" -ForegroundColor Cyan
Write-Host "   LocalOps Bot v0.1.0 Setup" -ForegroundColor Cyan
Write-Host "==============================================`n" -ForegroundColor Cyan

# --- Check for existing installation ---
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Warn "Service '$ServiceName' already exists (Status: $($existingService.Status))."
    $ans = Read-Host "Overwrite installation? This will stop and recreate the service (y/N)"
    if ($ans -ne 'y' -and $ans -ne 'Y') {
        Write-Host "Setup cancelled by user." -ForegroundColor Yellow
        exit 0
    }
}

# --- Step 0: Ensure binaries ---
if (-not $HasAgentBinaries) {
    Write-Step "Step 0/7: Downloading Agent binaries from GitHub..."
    $zipUrl = "https://github.com/$GitHubRepo/releases/latest/download/LocalOpsBot.Agent.zip"
    $zipPath = Join-Path $env:TEMP "LocalOpsBot.Agent.zip"
    $extractPath = Join-Path $env:TEMP "LocalOpsBot.Agent"
    try {
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
        if (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
        Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force
        $AgentSource = $extractPath
        $HasAgentBinaries = $true
        Write-Ok "Agent binaries downloaded and extracted"
    } catch {
        Write-ErrorExit "Failed to download Agent binaries: $_"
    }
} else {
    Write-Ok "Agent binaries found at $AgentSource"
}
if (-not $HasTrayBinaries) {
    Write-Step "Step 0/7: Downloading Tray binaries from GitHub..."
    $zipUrl = "https://github.com/$GitHubRepo/releases/latest/download/LocalOpsBot.Tray.zip"
    $zipPath = Join-Path $env:TEMP "LocalOpsBot.Tray.zip"
    $extractPath = Join-Path $env:TEMP "LocalOpsBot.Tray"
    try {
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
        if (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
        Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force
        $TraySource = $extractPath
        $HasTrayBinaries = $true
        Write-Ok "Tray binaries downloaded and extracted"
    } catch {
        Write-Warn "Failed to download Tray binaries: $_ (Tray install skipped)"
    }
} else {
    Write-Ok "Tray binaries found at $TraySource"
}

# --- Step 1: Collect configuration ---
Write-Step "Step 1/7: Telegram configuration"

if ($NoInteractive) {
    if (-not $Token) { $Token = [Environment]::GetEnvironmentVariable($EnvVarName, 'Machine') }
    if (-not $Token) { $Token = Read-Host "Bot token (--Token)" }
    if (-not $ChatId) { $ChatId = Read-Host "Chat ID (--ChatId)" }
} else {
    if (-not $Token) { $Token = Read-Token }
    if (-not $ChatId) { $ChatId = Read-ChatId }
}

if (-not $Token) { Write-ErrorExit "Telegram bot token is required." }
if (-not (Test-BotToken $Token)) { Write-ErrorExit "Invalid bot token format." }
if (-not $ChatId) { Write-ErrorExit "Chat ID is required." }
if (-not (Test-ChatId $ChatId)) { Write-ErrorExit "Invalid chat ID format." }

Write-Ok "Token: $($Token.Substring(0, [Math]::Min(8, $Token.Length)))..."
Write-Ok "Chat ID: $ChatId"

# --- Step 2: Create directories ---
Write-Step "Step 2/7: Creating directories"
@($AgentDir, $TrayDir, $ConfigDir, $DataDir, $LogDir) | ForEach-Object {
    New-Item -ItemType Directory -Force -Path $_ | Out-Null
}
Write-Ok "Directories created"

# --- Step 3: Copy binaries ---
Write-Step "Step 3/7: Copying binaries"
if ($HasAgentBinaries) {
    Copy-Item "$AgentSource\*" $AgentDir -Recurse -Force
    Write-Ok "Agent -> $AgentDir"
}
if ($HasTrayBinaries) {
    Copy-Item "$TraySource\*" $TrayDir -Recurse -Force
    Write-Ok "Tray  -> $TrayDir"
}

# --- Step 4: Create config ---
Write-Step "Step 4/7: Creating configuration"
if (Test-Path $ConfigFile) {
    $ans = Read-Host "Config already exists. Overwrite? (y/N)"
    if ($ans -eq 'y' -or $ans -eq 'Y') {
        Write-Config -Token $Token -ChatId $ChatId
    } else {
        Write-Warn "Existing config preserved"
    }
} else {
    Write-Config -Token $Token -ChatId $ChatId
}
if ($ConfigSampleSrc -and -not (Test-Path $ConfigSample)) {
    Copy-Item $ConfigSampleSrc $ConfigSample -Force
    Write-Ok "Example config copied to $ConfigSample"
}

# --- Step 5: Set environment variable ---
Write-Step "Step 5/7: Setting environment variable"
[Environment]::SetEnvironmentVariable($EnvVarName, $Token, 'Machine')
Write-Ok "$EnvVarName set at machine level"

# --- Step 6: Create service ---
Write-Step "Step 6/7: Creating Windows service"
if ($existingService) {
    Write-Host "  Stopping existing service..."
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
sc.exe create $ServiceName binPath= "`"$AgentExe`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
sc.exe description $ServiceName "Personal Windows PC monitoring bot for Telegram." | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
Start-Service $ServiceName
Write-Ok "Service '$ServiceName' created and started"

# --- Step 7: Register Tray auto-start ---
Write-Step "Step 7/7: Registering Tray auto-start"
if (Test-Path $TrayExe) {
    New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LocalOpsBot.Tray" -Value "`"$TrayExe`"" -PropertyType String -Force | Out-Null
    Write-Ok "Tray auto-start registered"
} else {
    Write-Warn "Tray binary not found — auto-start skipped"
}

# ============================================================
#  DONE
# ============================================================
Write-Host "`n==============================================" -ForegroundColor Cyan
Write-Host "  [DONE] LocalOpsBot installed successfully!" -ForegroundColor Green
Write-Host "==============================================`n" -ForegroundColor Cyan
Write-Host "  Agent     : $AgentDir"
Write-Host "  Tray      : $TrayDir"
Write-Host "  Config    : $ConfigFile"
Write-Host "  Data      : $DataDir"
Write-Host "  Logs      : $LogDir"
Write-Host "`n  Test your bot: send /ping on Telegram"
Write-Host "  View logs    : Get-Content '$LogDir\*.log' -Tail 50 -Wait"
Write-Host "  Uninstall    : $(Join-Path $ScriptRoot 'uninstall.ps1')`n"

Get-Service $ServiceName
