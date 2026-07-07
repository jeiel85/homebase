# Run as Administrator
$ServiceName = "LocalOpsBot.Agent"
$DisplayName = "LocalOpsBot Agent"
$InstallDir = "C:\Program Files\LocalOpsBot\Agent"
$ExePath = Join-Path $InstallDir "LocalOpsBot.Agent.exe"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Write-Error "Please run as Administrator."
    exit 1
}

if (-not (Test-Path $ExePath)) {
    Write-Error "Agent executable not found: $ExePath"
    exit 1
}

New-Item -ItemType Directory -Force -Path "C:\ProgramData\LocalOpsBot\config" | Out-Null
New-Item -ItemType Directory -Force -Path "C:\ProgramData\LocalOpsBot\data" | Out-Null
New-Item -ItemType Directory -Force -Path "C:\ProgramData\LocalOpsBot\logs" | Out-Null

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service already exists. Stopping..."
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $ServiceName binPath= "`"$ExePath`"" start= auto DisplayName= "`"$DisplayName`""
sc.exe description $ServiceName "Personal Windows PC monitoring bot for Telegram."
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000

Start-Service $ServiceName
Get-Service $ServiceName
