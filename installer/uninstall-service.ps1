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

# 3b) Remove the inbound rules (Tailscale, LAN) the Agent adds when mobile monitoring is enabled.
& netsh.exe advfirewall firewall delete rule name="Homebase Mobile (Tailscale)" 2>&1 | Out-Null
& netsh.exe advfirewall firewall delete rule name="Homebase Mobile (LAN)" 2>&1 | Out-Null

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

# 4b) Take the Claude Code phone-approval hook (DECISIONS M-48) out of each user's settings.json,
#     before the Tray that knows the format is deleted. Only our hook goes; the rest of the file stays
#     (the previous file is kept as settings.json.homebase.bak). A hook left behind would only be
#     refused at once, but it stays in the user's own file (#64).
$hookRoots = @($DefaultRoot)
if ($InstallDir) { $hookRoots += $InstallDir }
$trayExe = $hookRoots | Select-Object -Unique |
    ForEach-Object { Join-Path $_ "Tray\Homebase.Tray.exe" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
$profileRoot = Split-Path -Parent $env:PUBLIC
if ($trayExe -and $profileRoot) {
    Get-ChildItem -Path $profileRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $settings = Join-Path $_.FullName ".claude\settings.json"
        if ((Test-Path $settings) -and (Select-String -Path $settings -Pattern "X-Homebase-Token" -SimpleMatch -Quiet)) {
            $p = Start-Process -FilePath $trayExe -ArgumentList @("--remove-claude-hook", "`"$settings`"") -Wait -PassThru -WindowStyle Hidden
            if ($p.ExitCode -eq 0) { Write-Host "Removed the Claude Code hook from $settings" }
            else { Write-Warning "Couldn't remove the Claude Code hook from $settings (exit $($p.ExitCode))" }
        }
    }
}

# 4c) Take Explorer's "Send to > Homebase" (DECISIONS M-66) out of each user's Send To folder. The tray
#     puts it there per user; only a shortcut that runs Homebase.Tray.exe goes.
if ($profileRoot) {
    $shell = $null
    try { $shell = New-Object -ComObject WScript.Shell } catch { }
    Get-ChildItem -Path $profileRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $lnk = Join-Path $_.FullName "AppData\Roaming\Microsoft\Windows\SendTo\Homebase.lnk"
        if (-not (Test-Path $lnk)) { return }
        $target = $null
        try { if ($shell) { $target = $shell.CreateShortcut($lnk).TargetPath } } catch { }
        if ($target -and ((Split-Path -Leaf $target) -ieq "Homebase.Tray.exe")) {
            Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue
            Write-Host "Removed $lnk"
        }
    }
}

# 4d) The "files received" toast (DECISIONS M-67) registered, per user, its app id and the homebase: links that
#     run the tray. Users signed in now have their registry loaded; the installing user's keys also go with the
#     installer's own uninstall entries. Only keys that run Homebase.Tray.exe go.
Get-ChildItem -Path Registry::HKEY_USERS -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -match '^S-1-5-21-[\d-]+$' } | ForEach-Object {
        $classes = "Registry::HKEY_USERS\$($_.PSChildName)\Software\Classes"
        $command = (Get-ItemProperty -Path "$classes\homebase\shell\open\command" -ErrorAction SilentlyContinue).'(default)'
        if ($command -and $command -like '*Homebase.Tray.exe*') {
            Remove-Item -Path "$classes\homebase" -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path "$classes\AppUserModelId\Homebase.Tray" -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "Removed the Homebase toast links for $($_.PSChildName)"
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
