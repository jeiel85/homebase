<#
.SYNOPSIS
    Switches Homebase temperature monitoring to the full LibreHardware (WinRing0) sensor and
    registers the Defender exclusion it needs — or reverts to the default WMI sensor with -Disable.
.DESCRIPTION
    The default temperature backend is WMI/ACPI: it loads no kernel driver and is never flagged by
    antivirus, but exposes only coarse ACPI zone temperatures (and nothing at all on many desktops).
    The LibreHardware backend reads per-core CPU / GPU / board sensors, but opening it loads the
    WinRing0 kernel driver, which Microsoft Defender classifies as a vulnerable driver
    (VulnerableDriver:WinNT/Winring0).

    Enabling here does two things, then restarts the Agent service:
      1. Sets temperature.source = "LibreHardware" in the ProgramData config.
      2. Adds a Microsoft Defender path exclusion for the extracted driver so it is not quarantined.

    SECURITY NOTE: a Defender exclusion only suppresses the antivirus *detection*. If this machine
    also enforces the Microsoft vulnerable-driver blocklist (Memory Integrity / HVCI / Smart App
    Control — on by default on many Windows 11 systems), the driver is blocked from loading at the
    kernel level regardless of this exclusion, and temperatures will stay empty. In that case, keep
    the default WMI sensor.

    Run as Administrator.
.PARAMETER Disable
    Revert to the default WMI sensor: set temperature.source = "Wmi" and remove the Defender
    exclusion that the enable path added.
.PARAMETER InstallDir
    Install root whose Agent subfolder holds Homebase.Agent.exe (and the extracted .sys driver).
    Optional — when omitted, it is detected from the running service's binary path, falling back to
    C:\Program Files\Homebase.
.EXAMPLE
    .\enable-temperature.ps1
    Enable full sensors and register the Defender exclusion.
.EXAMPLE
    .\enable-temperature.ps1 -Disable
    Revert to the WMI sensor and remove the exclusion.
#>
#Requires -RunAsAdministrator
param(
    [switch]$Disable,
    [string]$InstallDir = ""
)

$ErrorActionPreference = 'Stop'

$ServiceName = "Homebase.Agent"
$ConfigDir   = "C:\ProgramData\Homebase\config"
$ConfigFile  = Join-Path $ConfigDir "appsettings.json"

# Resolve the Agent folder that holds Homebase.Agent.exe (and the extracted .sys driver). Prefer an
# explicit -InstallDir; otherwise read the running service's binary path so the exclusion matches the
# real driver even for custom install locations; fall back to the default install folder.
function Resolve-AgentDir {
    if ($InstallDir) { return (Join-Path $InstallDir "Agent") }
    try {
        $svc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
        if ($svc -and $svc.PathName) {
            $exe = $svc.PathName.Trim('"')
            if ($exe -match '^(?<p>.*?\.exe)') { $exe = $Matches['p'] }
            if (Test-Path $exe) { return (Split-Path -Parent $exe) }
        }
    } catch { }
    return (Join-Path "C:\Program Files\Homebase" "Agent")
}

$AgentDir   = Resolve-AgentDir
# LibreHardwareMonitor extracts the WinRing0 driver next to the host exe, named after it.
$DriverPath = Join-Path $AgentDir "Homebase.Agent.sys"

function Set-TemperatureSource($source) {
    # Edit only temperature.{enabled,source}, preserving the rest of appsettings.json.
    New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
    if (Test-Path $ConfigFile) {
        $json = Get-Content $ConfigFile -Raw | ConvertFrom-Json
    } else {
        $json = [pscustomobject]@{}
    }
    if (-not $json.temperature) {
        $json | Add-Member -NotePropertyName temperature -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    $json.temperature | Add-Member -NotePropertyName enabled -NotePropertyValue $true   -Force
    $json.temperature | Add-Member -NotePropertyName source  -NotePropertyValue $source -Force
    ($json | ConvertTo-Json -Depth 25) | Set-Content -Path $ConfigFile -Encoding UTF8
    Write-Host "Set temperature.source = $source in $ConfigFile"
}

function Restart-AgentService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        Restart-Service -Name $ServiceName -Force
        Write-Host "Restarted $ServiceName."
    } else {
        Write-Host "Service '$ServiceName' not found — the new setting applies on next start."
    }
}

# True when this PC is likely to block the WinRing0 driver from loading regardless of the Defender
# exclusion: the Microsoft vulnerable-driver blocklist and/or HVCI (Memory Integrity) is active.
function Test-DriverLoadLikelyBlocked {
    try {
        $v = Get-ItemPropertyValue -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config' -Name 'VulnerableDriverBlocklistEnable' -ErrorAction Stop
        if ($v -eq 1) { return $true }
    } catch { }
    try {
        $dg = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName Win32_DeviceGuard -ErrorAction Stop
        if ($dg.SecurityServicesRunning -contains 2) { return $true }  # 2 = HVCI / Memory Integrity
    } catch { }
    return $false
}

if ($Disable) {
    Set-TemperatureSource "Wmi"
    try {
        Remove-MpPreference -ExclusionPath $DriverPath -ErrorAction Stop
        Write-Host "Removed Defender exclusion for $DriverPath."
    } catch {
        Write-Host "No Defender exclusion removed (Defender unavailable or none set): $($_.Exception.Message)"
    }
    Restart-AgentService
    Write-Host "`nTemperature reverted to the default WMI sensor." -ForegroundColor Green
    exit 0
}

# --- Enable path ---
Write-Host "Enabling full (LibreHardware / WinRing0) temperature sensors." -ForegroundColor Cyan
Write-Host "This loads the WinRing0 kernel driver, which antivirus flags as vulnerable." -ForegroundColor Yellow

if (-not (Test-Path $AgentDir)) {
    Write-Warning "Agent folder not found at $AgentDir."
    Write-Warning "If you installed to a custom folder, re-run with:  -InstallDir <install root>"
}

Set-TemperatureSource "LibreHardware"

try {
    Add-MpPreference -ExclusionPath $DriverPath -ErrorAction Stop
    Write-Host "Added Defender exclusion for $DriverPath."
} catch {
    Write-Warning "Could not add Defender exclusion ($($_.Exception.Message))."
    Write-Warning "The driver may be quarantined. Add it manually, or keep the WMI sensor (-Disable)."
}

Restart-AgentService

Write-Host ""
if (Test-DriverLoadLikelyBlocked) {
    Write-Host "WARNING: this PC has the vulnerable-driver blocklist and/or Memory Integrity (HVCI) active." -ForegroundColor Red
    Write-Host "         The WinRing0 driver will very likely be BLOCKED from loading regardless of the" -ForegroundColor Red
    Write-Host "         Defender exclusion, so temperatures may stay empty. If they do, revert with:" -ForegroundColor Red
    Write-Host "           .\enable-temperature.ps1 -Disable" -ForegroundColor Red
} else {
    Write-Host "Full temperature sensors enabled." -ForegroundColor Green
    Write-Host "NOTE: if temperatures stay empty, this PC may still enforce the vulnerable-driver blocklist" -ForegroundColor Yellow
    Write-Host "      (Memory Integrity / HVCI / Smart App Control). Revert with:  .\enable-temperature.ps1 -Disable" -ForegroundColor Yellow
}
