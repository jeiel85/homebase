param(
    [Parameter(Mandatory=$true)]
    [string]$BotToken,

    [Parameter(Mandatory=$true)]
    [string]$ChatId
)

function Send-TelegramMessage {
    param([string]$Text)

    $uri = "https://api.telegram.org/bot$BotToken/sendMessage"
    Invoke-RestMethod -Uri $uri -Method Post -Body @{
        chat_id = $ChatId
        text    = $Text
    } | Out-Null
}

$computer = $env:COMPUTERNAME
$os = Get-CimInstance Win32_OperatingSystem
$bootTime = $os.LastBootUpTime
$uptime = (Get-Date) - $bootTime

$ip = Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike '169.254*' -and $_.IPAddress -ne '127.0.0.1' } |
    Select-Object -First 1 -ExpandProperty IPAddress

$text = @"
🟢 PC boot detected

PC: $computer
Boot: $bootTime
Uptime: $([int]$uptime.TotalMinutes) minutes
IP: $ip
"@

Send-TelegramMessage $text
