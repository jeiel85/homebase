param(
    [switch]$Purge
)

<#
.SYNOPSIS
    Uninstalls LocalOpsBot Agent service and optionally removes all data.
.DESCRIPTION
    Default (keep-data): removes service, startup registration, and binaries.
    Config/data/logs are preserved.
    
    -Purge: removes everything including config, database, and logs.
.PARAMETER Purge
    When specified, deletes config/data/log directories entirely.
#>

#Requires -RunAsAdministrator

$ServiceName = "LocalOpsBot.Agent"
$AgentDir = "C:\Program Files\LocalOpsBot\Agent"
$TrayDir = "C:\Program Files\LocalOpsBot\Tray"
$ProgramData = "C:\ProgramData\LocalOpsBot"

# --- Stop and remove service ---
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping service '$ServiceName'..."
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service removed." -ForegroundColor Green
} else {
    Write-Host "Service not found."
}

# --- Remove Tray auto-start ---
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LocalOpsBot.Tray" -ErrorAction SilentlyContinue
Write-Host "Tray auto-start removed."

# --- Remove binaries ---
if (Test-Path $AgentDir) {
    Remove-Item $AgentDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Agent binaries removed."
}
if (Test-Path $TrayDir) {
    Remove-Item $TrayDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Tray binaries removed."
}

# --- Purge or keep data ---
if ($Purge) {
    if (Test-Path $ProgramData) {
        Remove-Item $ProgramData -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "All LocalOpsBot data purged." -ForegroundColor Yellow
    }
    Write-Host "`nLocalOpsBot fully uninstalled (purge)." -ForegroundColor Green
} else {
    Write-Host "`nLocalOpsBot uninstalled (keep-data)." -ForegroundColor Green
    Write-Host "Config/data/logs preserved at: $ProgramData"
    Write-Host "Re-run with -Purge to remove them."
}
