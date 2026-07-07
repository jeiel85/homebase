<#
.SYNOPSIS
    Registers LocalOpsBot Tray app to auto-start on user login.
.DESCRIPTION
    Adds a HKCU Run registry entry for the current user.
    Run without elevation (current user scope).
.NOTES
    Run this script after install-service.ps1 to enable Tray auto-start.
#>

$TrayExe = "C:\Program Files\LocalOpsBot\Tray\LocalOpsBot.Tray.exe"

if (-not (Test-Path $TrayExe)) {
    Write-Error "Tray executable not found: $TrayExe`nInstall Tray first using install.ps1"
    exit 1
}

$current = (Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LocalOpsBot.Tray" -ErrorAction SilentlyContinue)."LocalOpsBot.Tray"

if ($current) {
    if ($current -eq "`"$TrayExe`"") {
        Write-Host "Tray auto-start already registered." -ForegroundColor Green
    } else {
        Write-Host "Updating Tray path: $current -> $TrayExe"
        New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LocalOpsBot.Tray" -Value "`"$TrayExe`"" -PropertyType String -Force | Out-Null
    }
} else {
    New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LocalOpsBot.Tray" -Value "`"$TrayExe`"" -PropertyType String -Force | Out-Null
    Write-Host "Tray auto-start registered for current user." -ForegroundColor Green
}

Write-Host "`nTray will start automatically on next login."
Write-Host "To start now: & `"$TrayExe`""
