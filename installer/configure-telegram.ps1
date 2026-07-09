<#
.SYNOPSIS
    Applies Telegram credentials for Homebase. Run elevated from the Tray onboarding.
.DESCRIPTION
    Sets the machine-level bot-token environment variable, writes the chat ID into the
    ProgramData config allowlist, and restarts the Agent service so it picks them up at startup.
    This is the one privileged step of the otherwise non-elevated onboarding.
.PARAMETER Token
    Telegram bot token from @BotFather.
.PARAMETER ChatId
    Numeric Telegram chat ID allowed to control the bot.
#>
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)][string]$Token,
    [Parameter(Mandatory = $true)][string]$ChatId
)

$ErrorActionPreference = 'Stop'

$ServiceName = "Homebase.Agent"
$EnvVarName  = "HOMEBASE_TELEGRAM_TOKEN"
$ConfigDir   = "C:\ProgramData\Homebase\config"
$ConfigFile  = Join-Path $ConfigDir "appsettings.json"

function Test-BotToken($t) { return $t -match '^\d{6,10}:[A-Za-z0-9_-]{30,}$' }
function Test-ChatId($c)   { return $c -match '^-?\d{4,}$' }

if (-not (Test-BotToken $Token)) { Write-Error "Invalid bot token format."; exit 1 }
if (-not (Test-ChatId $ChatId))  { Write-Error "Invalid chat ID format.";   exit 1 }

# 1) Token -> machine env var (config references it via the "ENV:" indirection).
[Environment]::SetEnvironmentVariable($EnvVarName, $Token, 'Machine')

# 2) Chat ID -> config allowlist, preserving the rest of the config.
New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
if (Test-Path $ConfigFile) {
    $json = Get-Content $ConfigFile -Raw | ConvertFrom-Json
} else {
    $json = [pscustomobject]@{}
}
if (-not $json.telegram) {
    $json | Add-Member -NotePropertyName telegram -NotePropertyValue ([pscustomobject]@{}) -Force
}
$json.telegram | Add-Member -NotePropertyName botToken       -NotePropertyValue "ENV:$EnvVarName" -Force
$json.telegram | Add-Member -NotePropertyName allowedChatIds -NotePropertyValue @([long]$ChatId)  -Force
($json | ConvertTo-Json -Depth 25) | Set-Content -Path $ConfigFile -Encoding UTF8

# 3) Restart the service so it re-reads the token and chat at startup.
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Restart-Service -Name $ServiceName -Force
    Write-Host "Telegram configured and $ServiceName restarted."
} else {
    Write-Host "Telegram configured. Service '$ServiceName' not found — it will use these on next start."
}
