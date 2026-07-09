param(
    [string]$InstallDir = "",
    [switch]$Purge
)

<#
.SYNOPSIS
    Fully removes the Homebase agent: stops and deletes the Windows service, unregisters
    auto-start, clears the bot-token env var, and removes binaries.
.DESCRIPTION
    The service is created with sc.exe (outside the installer's file tracking), so plain
    "uninstall" leaves it running. This script removes it. It runs from the installer's
    [UninstallRun] step and can also be run by hand.

    Default (keep-data): removes service, auto-start, env var, and binaries; keeps config/data/logs.
    -Purge: also deletes config/data/logs.
.PARAMETER InstallDir
    The install root (Inno {app}); its Agent/Tray subfolders are removed in addition to the
    default C:\Program Files\Homebase location.
.PARAMETER Purge
    Also delete C:\ProgramData\Homebase (config, database, logs).
#>

#Requires -RunAsAdministrator

$ServiceName = "Homebase.Agent"
$EnvVarName  = "HOMEBASE_TELEGRAM_TOKEN"
$ProgramData = "C:\ProgramData\Homebase"
$DefaultRoot = "C:\Program Files\Homebase"

# 1) Kill running processes so files unlock and alerts stop immediately.
Get-Process -Name 'Homebase.Agent', 'Homebase.Tray' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# 2) Stop and delete the Windows service (the part a plain uninstall leaves behind).
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping service '$ServiceName'..."
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Service removed." -ForegroundColor Green
} else {
    Write-Host "Service not found."
}

# 3) Remove Tray auto-start and the machine-level bot-token env var.
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Homebase.Tray" -ErrorAction SilentlyContinue
[Environment]::SetEnvironmentVariable($EnvVarName, $null, 'Machine')
Write-Host "Tray auto-start and bot-token env var removed."

# 4) Remove the Defender exclusion that enable-temperature.ps1 may have added for the driver.
$driverRoots = @($DefaultRoot)
if ($InstallDir) { $driverRoots += $InstallDir }
foreach ($dr in ($driverRoots | Select-Object -Unique)) {
    $sys = Join-Path $dr "Agent\Homebase.Agent.sys"
    try {
        $current = (Get-MpPreference -ErrorAction Stop).ExclusionPath
        if ($current -and ($current -contains $sys)) {
            Remove-MpPreference -ExclusionPath $sys -ErrorAction Stop
            Write-Host "Removed Defender exclusion for $sys"
        }
    } catch {
        # Defender cmdlets unavailable (e.g. third-party AV) — nothing to clean up.
    }
}

# 5) Remove binaries from the default location and, if given, the actual install dir.
$roots = @($DefaultRoot)
if ($InstallDir) { $roots += $InstallDir }
foreach ($root in ($roots | Select-Object -Unique)) {
    foreach ($sub in 'Agent', 'Tray') {
        $p = Join-Path $root $sub
        if (Test-Path $p) {
            Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "Removed $p"
        }
    }
}

# 6) Purge or keep data.
if ($Purge) {
    if (Test-Path $ProgramData) {
        Remove-Item $ProgramData -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "All Homebase data purged." -ForegroundColor Yellow
    }
    Write-Host "`nHomebase fully uninstalled (purge)." -ForegroundColor Green
} else {
    Write-Host "`nHomebase uninstalled (keep-data)." -ForegroundColor Green
    Write-Host "Config/data/logs preserved at: $ProgramData"
    Write-Host "Re-run with -Purge to remove them."
}
