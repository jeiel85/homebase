param(
    [switch]$Purge
)

$ServiceName = "LocalOpsBot.Agent"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Write-Error "Please run as Administrator."
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
}

Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LocalOpsBot.Tray" -ErrorAction SilentlyContinue

if ($Purge) {
    Remove-Item "C:\ProgramData\LocalOpsBot" -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Uninstalled LocalOpsBot.Agent. Program files can be removed manually if needed."
