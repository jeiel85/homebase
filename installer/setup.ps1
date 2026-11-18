<#
.SYNOPSIS
    Installs Homebase (Agent + Tray) with interactive configuration.
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
    irm https://github.com/jeiel85/homebase/releases/latest/download/bootstrap.ps1 | iex
#>

#Requires -RunAsAdministrator
param(
    [string]$Token = "",
    [string]$ChatId = "",
    [switch]$NoInteractive,
    [switch]$SkipTelegram,
    [string]$InstallDir = "",
    [string]$AgentSource = "",
    [string]$TraySource = ""
)

# --- Constants ---
$ServiceName  = "Homebase.Agent"
$DisplayName  = "Homebase"
# When the installer passes -InstallDir ({app}), run the service and tray from there — a single,
# installer-tracked location. Standalone (.zip) installs fall back to Program Files.
if ($InstallDir) { $InstallRoot = $InstallDir } else { $InstallRoot = "C:\Program Files\Homebase" }
$AgentDir     = Join-Path $InstallRoot "Agent"
$TrayDir      = Join-Path $InstallRoot "Tray"
$AgentExe     = Join-Path $AgentDir "Homebase.Agent.exe"
$TrayExe      = Join-Path $TrayDir "Homebase.Tray.exe"
$ConfigDir    = "C:\ProgramData\Homebase\config"
$DataDir      = "C:\ProgramData\Homebase\data"
$LogDir       = "C:\ProgramData\Homebase\logs"
$ProgramData  = "C:\ProgramData\Homebase"
$EnvVarName   = "HOMEBASE_TELEGRAM_TOKEN"
$ConfigFile   = Join-Path $ConfigDir "appsettings.json"
$ConfigSample = Join-Path $ConfigDir "appsettings.example.json"
$GitHubRepo   = "jeiel85/homebase"

# --- Detect source paths ---
$ScriptRoot = Split-Path -Parent $PSCommandPath
if (-not $AgentSource) {
    $candidate = Join-Path $ScriptRoot "..\Agent"
    if (Test-Path $candidate) { $AgentSource = Resolve-Path $candidate }
}
if (-not $AgentSource -or -not (Test-Path (Join-Path $AgentSource "Homebase.Agent.exe"))) {
    $candidate = Join-Path $ScriptRoot "Agent"
    if (Test-Path $candidate) { $AgentSource = Resolve-Path $candidate }
}
if (-not $TraySource) {
    $candidate = Join-Path $ScriptRoot "..\Tray"
    if (Test-Path $candidate) { $TraySource = Resolve-Path $candidate }
}
if (-not $TraySource -or -not (Test-Path (Join-Path $TraySource "Homebase.Tray.exe"))) {
    $candidate = Join-Path $ScriptRoot "Tray"
    if (Test-Path $candidate) { $TraySource = Resolve-Path $candidate }
}

$HasAgentBinaries = $AgentSource -and (Test-Path (Join-Path $AgentSource "Homebase.Agent.exe"))
$HasTrayBinaries  = $TraySource  -and (Test-Path (Join-Path $TraySource  "Homebase.Tray.exe"))

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

function Set-DataDirAcl($Path) {
    # Same ACL the Agent applies at service start (DatabaseMigrationService): the database holds
    # relayed notification text, so only SYSTEM and Administrators may open the folder. Applied here
    # too so the folder is closed before the service first starts. Well-known SIDs, not names, so it
    # works on any Windows language. Inheritance from ProgramData (which lets Users read) is cut; the
    # files already inside inherit the new entries. SYSTEM keeps full control, so the service can't
    # be locked out. A failure only warns: the Agent applies the same ACL when it starts.
    & icacls.exe $Path /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /C /Q | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "Data folder limited to SYSTEM and Administrators"
    } else {
        Write-Warn "Could not limit access to $Path (icacls exit code $LASTEXITCODE); the service retries at start."
    }
}

function Set-ProgramDataAcl($Root, $Logs) {
    # Same rule the Agent applies at service start (ProgramDataAcl): ProgramData lets every user create
    # files and folders, but the service (SYSTEM) reads its config here, so standard users only read —
    # except logs\, where each user's Tray creates its own log file (files only, no folders).
    & icacls.exe $Root /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" /C /Q | Out-Null
    $rootOk = $LASTEXITCODE -eq 0
    & icacls.exe $Logs /grant "*S-1-5-32-545:(WD,WA,WEA)" "*S-1-3-0:(OI)(IO)F" /C /Q | Out-Null
    if ($rootOk -and $LASTEXITCODE -eq 0) {
        Write-Ok "Homebase's ProgramData folder limited to SYSTEM and Administrators (users read)"
    } else {
        Write-Warn "Could not limit access to $Root (icacls exit code $LASTEXITCODE); the service retries at start."
    }
}

function Set-InstallDirAcl($Path) {
    # The service runs $Path\Agent\Homebase.Agent.exe as SYSTEM. Under Program Files the folder already
    # inherits "users read"; a folder picked elsewhere (D:\Homebase) inherits the drive root's ACL,
    # which on most drives lets every user change files — and so replace what SYSTEM runs.
    $pf = [Environment]::GetFolderPath('ProgramFiles').TrimEnd('\') + '\'
    if ((Join-Path $Path '').StartsWith($pf, [StringComparison]::OrdinalIgnoreCase)) { return }
    & icacls.exe $Path /setowner "*S-1-5-32-544" /T /C /Q | Out-Null
    & icacls.exe $Path /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" /T /C /Q | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "Install folder limited to SYSTEM and Administrators (users read and run)"
    } else {
        Write-Warn "Could not limit access to $Path (icacls exit code $LASTEXITCODE). Standard users may be able to change the service's files."
    }
}

function Test-TrustedOwner($Path) {
    # A config a standard user created before Homebase was installed stays theirs to change.
    try { $sid = (Get-Acl -LiteralPath $Path).GetOwner([System.Security.Principal.SecurityIdentifier]).Value }
    catch { return $false }
    if ($sid -in @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')) { return $true }
    # Some PCs make the administrator's own account the owner of what it creates elevated.
    try { return [bool](Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop | Where-Object { $_.SID.Value -eq $sid }) }
    catch { return $true } # membership unreadable (domain-joined PCs): keep the user's config rather than lose it
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
    param($ChatId)
    # Build appsettings.json from the shipped example so the schema always
    # matches exactly what the Agent binds (telegram.botToken / allowedChatIds /
    # agent.databasePath / collectors / ...). Only the chat allowlist and DB path
    # are injected here; the bot token stays out of the file and is resolved at
    # runtime from the machine env var via the "ENV:" indirection.
    $dbPath = (Join-Path $DataDir "localops.db")
    # Empty chat allowlist is valid: a fresh install stays unconfigured until onboarding sets it.
    $chatIds = if ($ChatId) { @([long]$ChatId) } else { @() }
    $chatJson = if ($ChatId) { "[$ChatId]" } else { "[]" }
    if ($ConfigSampleSrc -and (Test-Path $ConfigSampleSrc)) {
        $json = Get-Content $ConfigSampleSrc -Raw | ConvertFrom-Json
        $json.telegram.botToken       = "ENV:$EnvVarName"
        $json.telegram.allowedChatIds = $chatIds
        $json.agent.databasePath      = $dbPath
        ($json | ConvertTo-Json -Depth 25) | Set-Content -Path $ConfigFile -Encoding UTF8
        Write-Ok "Configuration written to $ConfigFile (from example schema)"
    } else {
        $dbFwd = $dbPath.Replace('\', '/')
        $config = @"
{
  "telegram": {
    "botToken": "ENV:$EnvVarName",
    "allowedChatIds": $chatJson,
    "pollingTimeoutSeconds": 30
  },
  "agent": {
    "machineDisplayName": "$env:COMPUTERNAME",
    "sendBootNotification": true,
    "databasePath": "$dbFwd"
  },
  "notificationForwarding": { "enabled": true }
}
"@
        $config | Set-Content -Path $ConfigFile -Encoding UTF8
        Write-Ok "Configuration written to $ConfigFile (minimal fallback)"
    }
}

# ============================================================
#  MAIN
# ============================================================

$verText = "v?"
if ($HasAgentBinaries) {
    try {
        $pv = (Get-Item (Join-Path $AgentSource "Homebase.Agent.exe")).VersionInfo.ProductVersion
        if ($pv) { $verText = "v$pv" }
    } catch { }
}
Write-Host "`n==============================================" -ForegroundColor Cyan
Write-Host "   Homebase $verText Setup" -ForegroundColor Cyan
Write-Host "==============================================`n" -ForegroundColor Cyan

# --- Check for existing installation ---
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Warn "Service '$ServiceName' already exists (Status: $($existingService.Status))."
    if ($NoInteractive -or $SkipTelegram) {
        # Unattended (installer) path: never block on a prompt — reinstall in place.
        Write-Warn "Reinstalling unattended: the service will be stopped and recreated."
    } else {
        $ans = Read-Host "Overwrite installation? This will stop and recreate the service (y/N)"
        if ($ans -ne 'y' -and $ans -ne 'Y') {
            Write-Host "Setup cancelled by user." -ForegroundColor Yellow
            exit 0
        }
    }
}

# --- Step 0: Verify binaries are present ---
# setup.ps1 always ships inside the Homebase-Setup package (the .zip payload or
# the Setup.exe install dir), so the Agent/ and Tray/ folders sit next to it.
# There are no standalone Agent/Tray zips to fall back to anymore.
Write-Step "Step 0/7: Verifying binaries"
if ($HasAgentBinaries) {
    Write-Ok "Agent binaries found at $AgentSource"
} else {
    Write-Host "  [X] Agent binaries not found next to setup.ps1." -ForegroundColor Red
    Write-Host "      Download the full 'Homebase-Setup.zip', extract it, then run setup.ps1" -ForegroundColor Yellow
    Write-Host "      from the extracted folder. Or use the one-liner installer instead:" -ForegroundColor Yellow
    Write-Host "      irm https://github.com/$GitHubRepo/releases/latest/download/bootstrap.ps1 | iex" -ForegroundColor Yellow
    exit 1
}
if ($HasTrayBinaries) {
    Write-Ok "Tray binaries found at $TraySource"
} else {
    Write-Warn "Tray binaries not found — Tray install will be skipped"
}

# --- Step 1: Collect configuration ---
Write-Step "Step 1/7: Telegram configuration"

if ($SkipTelegram) {
    Write-Ok "Skipping Telegram setup — configure it on first run from the Homebase tray."
    $Token = ""
    $ChatId = ""
} else {
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
}

# --- Step 2: Create directories ---
Write-Step "Step 2/7: Creating directories"
@($AgentDir, $TrayDir, $ConfigDir, $DataDir, $LogDir) | ForEach-Object {
    New-Item -ItemType Directory -Force -Path $_ | Out-Null
}
Write-Ok "Directories created"
Set-ProgramDataAcl $ProgramData $LogDir
Set-DataDirAcl $DataDir
Set-InstallDirAcl $InstallRoot

# --- Step 3: Copy binaries ---
Write-Step "Step 3/7: Copying binaries"
if ($InstallDir) {
    # In-place install: the installer already placed the binaries under {app}; nothing to copy.
    Write-Ok "Using in-place binaries under $InstallRoot"
} else {
    if ($HasAgentBinaries) {
        Copy-Item "$AgentSource\*" $AgentDir -Recurse -Force
        Write-Ok "Agent -> $AgentDir"
    }
    if ($HasTrayBinaries) {
        Copy-Item "$TraySource\*" $TrayDir -Recurse -Force
        Write-Ok "Tray  -> $TrayDir"
    }
}

# --- Step 4: Create config ---
Write-Step "Step 4/7: Creating configuration"
if ((Test-Path $ConfigFile) -and -not (Test-TrustedOwner $ConfigFile)) {
    # Not created by an administrator or the service: whoever made it could have chosen what SYSTEM
    # runs (cloudflared's path, say). Kept aside for reference, never used.
    $aside = "$ConfigFile.untrusted-$(Get-Date -Format yyyyMMddHHmmss)"
    Move-Item -LiteralPath $ConfigFile -Destination $aside -Force
    & icacls.exe $aside /setowner "*S-1-5-32-544" /C /Q | Out-Null
    & icacls.exe $aside /reset /C /Q | Out-Null
    Write-Warn "Existing config was not created by an administrator; moved to $aside and starting fresh"
}
if (Test-Path $ConfigFile) {
    if ($SkipTelegram) {
        # Keep a prior onboarding-set config on reinstall; never prompt in this unattended path.
        Write-Warn "Existing config preserved (configure Telegram from the tray)"
    } else {
        $ans = Read-Host "Config already exists. Overwrite? (y/N)"
        if ($ans -eq 'y' -or $ans -eq 'Y') {
            Write-Config -ChatId $ChatId
        } else {
            Write-Warn "Existing config preserved"
        }
    }
} else {
    Write-Config -ChatId $ChatId
}
if ($ConfigSampleSrc -and -not (Test-Path $ConfigSample)) {
    Copy-Item $ConfigSampleSrc $ConfigSample -Force
    Write-Ok "Example config copied to $ConfigSample"
}

# --- Step 5: Set environment variable ---
Write-Step "Step 5/7: Setting environment variable"
if ($Token) {
    [Environment]::SetEnvironmentVariable($EnvVarName, $Token, 'Machine')
    Write-Ok "$EnvVarName set at machine level"
} else {
    Write-Warn "No token yet — skipping env var (onboarding sets it on first run)."
}

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
    New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Homebase.Tray" -Value "`"$TrayExe`"" -PropertyType String -Force | Out-Null
    Write-Ok "Tray auto-start registered"
} else {
    Write-Warn "Tray binary not found — auto-start skipped"
}

# ============================================================
#  DONE
# ============================================================
Write-Host "`n==============================================" -ForegroundColor Cyan
Write-Host "  [DONE] Homebase installed successfully!" -ForegroundColor Green
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
