using System.Globalization;

namespace LocalOpsBot.Tray;

/// <summary>
/// Tray UI text in English and Korean. Korean is used when the UI culture is Korean — the same
/// rule as the bot's <c>LocalOpsBot.Core.Localization.Strings</c>; <see cref="App"/> applies the
/// <c>agent:language</c> setting to the UI culture at startup so tray and bot always match.
/// XAML reads these with <c>{x:Static local:L.Name}</c>. English chrome labels are uppercase
/// on purpose (the tray's console voice); Korean has no case.
/// </summary>
public static class L
{
    private static bool Ko => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

    private static string T(string en, string ko) => Ko ? ko : en;

    // ── Tray menu ────────────────────────────────────────────────────────────
    public static string MenuOpen => T("Open Homebase", "Homebase 열기");
    public static string MenuSettings => T("Settings…", "설정…");
    public static string MenuSetup => T("Setup checklist", "처음 설정 다시 보기");
    public static string MenuExit => T("Exit", "종료");
    public static string MenuCheckUpdates(string version) => T($"Check for updates (v{version})", $"업데이트 확인 (v{version})");
    public static string MenuUpdateAvailable(object version) => T($"Install update v{version}", $"새 버전 v{version} 설치");

    // ── Common ───────────────────────────────────────────────────────────────
    public static string Close => T("CLOSE", "닫기");
    public static string Cancel => T("Cancel", "취소");
    public static string Ok => T("OK", "확인");
    public static string Later => T("Later", "나중에");
    public static string RestartNow => T("Restart now", "지금 다시 시작");
    public static string Refresh => T("REFRESH", "새로고침");
    public static string Recheck => T("RE-CHECK", "다시 확인");
    public static string SendTest => T("SEND TEST MESSAGE", "테스트 메시지 보내기");
    public static string Sending => T("Sending…", "보내는 중…");
    public static string AdminNote => T("Needs administrator approval", "관리자 승인 필요");
    public static string ChipReady => T("READY", "준비됨");
    public static string ChipSetup => T("SET UP", "설정 필요");
    public static string ChipMissing => T("MISSING", "없음");
    public static string ChipOptional => T("OPTIONAL", "선택");
    public static string ChipOn => T("ON", "켜짐");
    public static string ChipOff => T("OFF", "꺼짐");

    // ── Dashboard ────────────────────────────────────────────────────────────
    public static string DashEyebrow => T("PC MONITOR", "PC 모니터");
    public static string DashThisPc => T("THIS PC", "지금 이 PC");
    public static string DashDisks => T("DISKS", "디스크");
    public static string DashConnections => T("CONNECTIONS", "연결");
    public static string DashUptime => T("UPTIME", "가동");
    public static string DashMemory => T("RAM", "메모리");
    public static string DashNetwork => T("NETWORK", "네트워크");
    public static string DashTelegram => T("Telegram", "텔레그램");
    public static string DashForwarding => T("Notification forwarding", "알림 전달");
    public static string DashPhone => T("Phone", "휴대폰");
    public static string DashOpenSettings => T("SETTINGS", "설정 열기");
    public static string BadgeChecking => T("checking…", "확인 중…");
    public static string BadgeRunning => T("● Monitoring", "● 감시 중");
    public static string BadgeNotInstalled => T("● Service not installed", "● 감시 서비스 없음");
    public static string BadgeStopped(string state) => T($"● Service {state.ToLowerInvariant()}", "● 감시 서비스 멈춤");
    public static string Unknown => T("unknown", "알 수 없음");
    public static string Online(string ip) => T($"Online ({ip})", $"온라인 ({ip})");
    public static string NoIp => T("no IP", "IP 없음");
    public static string Offline => T("Offline", "오프라인");
    public static string DiskFree(double freeGb, double totalGb) =>
        T($"{freeGb:F0} GB free / {totalGb:F0} GB", $"{freeGb:F0} GB 남음 / {totalGb:F0} GB");
    public static string UptimeDays(int d, int h, int m) => T($"{d}d {h:00}h {m:00}m", $"{d}일 {h}시간 {m}분");
    public static string UptimeHours(int h, int m) => T($"{h:00}h {m:00}m", $"{h}시간 {m}분");
    public static string TelegramConnected(long chatId) => T($"Connected — chat {chatId}", $"연결됨 — 채팅 {chatId}");
    public static string TelegramNotSetUp => T("Not set up — open Settings", "설정 필요 — 설정에서 연결하세요");
    public static string ForwardingOn => T("On — Windows notifications go to your phone", "켜짐 — Windows 알림을 휴대폰으로 보냄");
    public static string ForwardingOff => T("Off", "꺼짐");
    public static string PhoneOn => T("On — pair in Settings › Phone", "켜짐 — 설정 › 휴대폰에서 기기 추가");
    public static string PhoneOff => T("Off — turn on in Settings › Phone", "꺼짐 — 설정 › 휴대폰에서 켜기");

    // ── Settings window ──────────────────────────────────────────────────────
    public static string SettingsTitle => T("Homebase — Settings", "Homebase — 설정");
    public static string SettingsEyebrow => T("SETTINGS", "설정");
    public static string TabPhone => T("Phone", "휴대폰");
    public static string TabTelegram => T("Telegram", "텔레그램");
    public static string TabForwarding => T("Notifications", "알림 전달");
    public static string TabMonitors => T("Watch list", "감시 대상");
    public static string TabUpdates => T("Updates", "업데이트");

    public static string TgHeader => T("TELEGRAM CONNECTION", "텔레그램 연결");
    public static string TgWhat => T(
        "The bot is how this PC reports to you and takes your commands (/status, /process …).",
        "봇은 이 PC 상태를 알려 주고, 내 채팅에서 /status, /process 같은 명령을 받습니다.");
    public static string TgToken => T("Bot token", "봇 토큰");
    public static string TgTokenHint => T("From @BotFather — looks like 123456:ABC-DEF…", "@BotFather가 준 값 — 123456:ABC-DEF… 형태");
    public static string TgChatId => T("Chat ID", "채팅 ID");
    public static string TgChatIdHint => T("Your chat's number, e.g. 123456789", "내 채팅 번호, 예: 123456789");
    public static string TgFirstTime => T("FIRST TIME?", "처음이라면");
    public static string TgStep1 => T("Create a bot with @BotFather and copy its token.", "@BotFather에서 봇을 만들고 토큰을 복사합니다.");
    public static string TgStep2 => T("Send your bot any message so it can see your chat.", "만든 봇에게 아무 메시지나 보냅니다.");
    public static string TgStep3 => T("Paste the token and your chat ID above, then save.", "위에 토큰과 채팅 ID를 넣고 저장합니다.");
    public static string TgOpenBotFather => T("Open @BotFather", "@BotFather 열기");
    public static string TgSaveNote => T(
        "Saving asks for administrator approval once and restarts the monitoring service (a few seconds).",
        "저장하면 관리자 승인 창이 한 번 뜨고 감시 서비스가 다시 시작됩니다(몇 초).");
    public static string TgSave => T("SAVE", "저장");
    public static string TgStatusReady(long chatId) => T(
        $"Connected — commands are accepted from chat {chatId}.",
        $"연결됨 — 채팅 {chatId}에서 명령을 받습니다.");
    public static string TgStatusNoToken => T("No bot token yet.", "아직 봇 토큰이 없습니다.");
    public static string TgStatusNoChat => T("Token is set, but no chat ID yet.", "토큰은 있지만 채팅 ID가 없습니다.");
    public static string TgServiceNotRunning => T(
        " The monitoring service isn't running, so the bot won't answer.",
        " 감시 서비스가 실행 중이 아니라 봇이 응답하지 않습니다.");
    public static string TgInvalidToken => T("Enter a valid bot token (looks like 123456:ABC-DEF…).", "올바른 봇 토큰을 넣으세요(123456:ABC-DEF… 형태).");
    public static string TgInvalidChat => T("Enter a numeric chat ID (e.g. 123456789).", "숫자로 된 채팅 ID를 넣으세요(예: 123456789).");
    public static string TgScriptMissing => T("configure-telegram.ps1 not found next to the app — reinstall Homebase.", "앱 옆에 configure-telegram.ps1이 없습니다. Homebase를 다시 설치하세요.");
    public static string TgApplying => T("Saving — approve the administrator prompt…", "저장하는 중 — 관리자 승인 창에서 허용하세요…");
    public static string TgHelperFailed => T("Couldn't start the configuration helper.", "설정 도우미를 실행하지 못했습니다.");
    public static string TgSaved => T("Saved. The monitoring service is restarting — re-checking…", "저장했습니다. 감시 서비스를 다시 시작하는 중 — 다시 확인합니다…");
    public static string TgApplyFailed(int code) => T($"Not saved (exit {code}). Check the token and chat ID.", $"저장하지 못했습니다(종료 코드 {code}). 토큰과 채팅 ID를 확인하세요.");
    public static string TgCancelled => T("Not saved — administrator approval was declined.", "저장하지 않았습니다 — 관리자 승인이 취소되었습니다.");
    public static string Failed(string message) => T($"Failed: {message}", $"실패: {message}");

    public static string FwHeader => T("FORWARD WINDOWS NOTIFICATIONS", "Windows 알림을 휴대폰으로");
    public static string FwWhat => T(
        "Sends the title and text of notifications that pop up on this PC to the Homebase app as an end-to-end encrypted push (pair a phone in the Phone tab). They never go to Telegram by default. Values that look like codes, passwords or tokens are masked.",
        "이 PC에 뜨는 알림의 제목과 내용을 Homebase 앱으로 종단 암호화 푸시해 보냅니다(휴대폰 탭에서 휴대폰을 연결하세요). 기본값으로는 텔레그램으로 보내지 않습니다. 인증 코드·비밀번호·토큰처럼 보이는 값은 가려서 보냅니다.");
    public static string FwToggle => T("Forward notifications to my phone", "알림을 휴대폰으로 보내기");
    public static string FwApply => T("APPLY", "적용");
    public static string FwApplyNote => T(
        "Applying asks for administrator approval, then the tray restarts to start or stop listening.",
        "적용하면 관리자 승인 후 트레이가 다시 시작되어 알림 수신을 켜거나 끕니다.");
    public static string FwCurrent(bool on) => T(on ? "Currently on." : "Currently off.", on ? "지금 켜져 있습니다." : "지금 꺼져 있습니다.");
    public static string FwAppsHeader => T("APPS TO FORWARD", "보낼 앱");
    public static string FwAppsWhat => T(
        "Checked apps are forwarded. Apps seen for the first time are forwarded too. Uncheck a phone-mirroring app (e.g. Phone Link) to stop notifications looping back. Saves instantly — no approval needed.",
        "체크한 앱만 보냅니다. 처음 보이는 앱은 자동으로 보냅니다. 휴대폰 알림이 되돌아오는 고리를 막으려면 휴대폰 미러링 앱(예: 휴대폰과 연결)을 끄세요. 승인 없이 바로 적용됩니다.");
    public static string FwAppsOffHint => T("Turn forwarding on to choose apps.", "알림 전달을 켜면 앱을 고를 수 있습니다.");
    public static string FwNoApps => T("No apps seen yet. Wait for a notification, then press Refresh.", "아직 본 앱이 없습니다. 알림이 한 번 뜬 뒤 새로고침을 누르세요.");
    public static string FwSave => T("SAVE", "저장");
    public static string FwSavedAll => T("Saved — forwarding every app.", "저장했습니다 — 모든 앱을 보냅니다.");
    public static string FwSavedSome(int n) => T($"Saved — {n} app(s) not forwarded. Applies within seconds.", $"저장했습니다 — {n}개 앱은 보내지 않습니다. 몇 초 안에 적용됩니다.");
    public static string FwConfirmTitle(bool enable) => T(enable ? "Turn on notification forwarding" : "Turn off notification forwarding", enable ? "알림 전달 켜기" : "알림 전달 끄기");
    public static string FwNotSaved => T("Not saved — administrator approval is required. Nothing changed.", "저장하지 않았습니다 — 관리자 승인이 필요합니다. 바뀐 것은 없습니다.");
    public static string FwRestart(bool enable) => T(
        enable ? "Forwarding is on. Restart the tray now to start listening?" : "Forwarding is off. Restart the tray now to stop listening?",
        enable ? "알림 전달을 켰습니다. 수신을 시작하려면 트레이를 다시 시작해야 합니다. 지금 다시 시작할까요?" : "알림 전달을 껐습니다. 수신을 멈추려면 트레이를 다시 시작해야 합니다. 지금 다시 시작할까요?");

    public static string MonHeader => T("WATCH LIST", "감시 대상");
    public static string MonWhat => T(
        "Send /process, /services, /http or /ports in Telegram and the bot reports whether each item below is alive. One per line.",
        "텔레그램에서 /process, /services, /http, /ports를 보내면 아래 대상이 살아 있는지 알려 줍니다. 한 줄에 하나씩 적습니다.");
    public static string MonProcesses => T("PROGRAMS (process name)", "프로그램 (프로세스 이름)");
    public static string MonProcessesEx => T("e.g. chrome", "예: chrome");
    public static string MonServices => T("WINDOWS SERVICES", "Windows 서비스");
    public static string MonServicesEx => T("e.g. Spooler", "예: Spooler");
    public static string MonHttp => T("WEB ADDRESSES", "웹 주소");
    public static string MonHttpEx => T("e.g. https://example.com/health", "예: https://example.com/health");
    public static string MonTcp => T("PORTS (host:port)", "포트 (호스트:포트)");
    public static string MonTcpEx => T("e.g. localhost:5432", "예: localhost:5432");
    public static string MonSave => T("SAVE", "저장");
    public static string MonSaveNote => T(
        "Saving asks for administrator approval and restarts the monitoring service.",
        "저장하면 관리자 승인 후 감시 서비스가 다시 시작됩니다.");
    public static string MonDirty => T("● Unsaved changes", "● 저장하지 않은 변경 있음");
    public static string MonSaving => T("Saving — approve the administrator prompt…", "저장하는 중 — 관리자 승인 창에서 허용하세요…");
    public static string MonSaved => T("Saved. The monitoring service is restarting to apply.", "저장했습니다. 감시 서비스가 다시 시작되며 적용됩니다.");
    public static string MonNotSaved => T("Not saved — administrator approval is required.", "저장하지 않았습니다 — 관리자 승인이 필요합니다.");

    public static string UpdHeader => T("UPDATES", "업데이트");
    public static string UpdCurrent(string v) => T($"Installed version: v{v}", $"설치된 버전: v{v}");
    public static string UpdCheck => T("CHECK FOR UPDATES", "업데이트 확인");
    public static string UpdWhat => T(
        "Checks GitHub Releases for a newer version. Installing asks for administrator approval.",
        "GitHub Releases에서 새 버전을 찾습니다. 설치할 때 관리자 승인이 필요합니다.");
    public static string AboutHeader => T("ABOUT", "정보");
    public static string AboutLine => T("Personal Windows PC monitoring over Telegram and your phone.", "텔레그램과 휴대폰으로 보는 개인 Windows PC 모니터링");

    // ── Setup checklist (onboarding) ─────────────────────────────────────────
    public static string SetupTitle => T("Homebase — Setup", "Homebase — 처음 설정");
    public static string SetupEyebrow => T("SETUP", "처음 설정");
    public static string SetupIntro => T(
        "Here's what's ready on this PC and what still needs a step. Each item says what to do next.",
        "이 PC에서 준비된 것과 아직 남은 것을 확인합니다. 항목마다 다음에 할 일을 알려 줍니다.");
    public static string SetupRequired => T("REQUIRED", "필수");
    public static string SetupOptional => T("OPTIONAL", "선택");
    public static string SetupService => T("Monitoring service", "감시 서비스");
    public static string SetupServiceRunning => T("Running — monitoring is active.", "실행 중 — 감시하고 있습니다.");
    public static string SetupServiceStopped => T("Installed but stopped. Start the Homebase.Agent service.", "설치됐지만 멈춰 있습니다. Homebase.Agent 서비스를 시작하세요.");
    public static string SetupServiceMissing => T("Not installed. Run the Homebase installer.", "설치되지 않았습니다. Homebase 설치 프로그램을 실행하세요.");
    public static string SetupTelegram => T("Telegram connection", "텔레그램 연결");
    public static string SetupConnectTelegram => T("CONNECT TELEGRAM", "텔레그램 연결하기");
    public static string SetupChangeTelegram => T("CHANGE", "바꾸기");
    public static string SetupAi => T("AI advice (Ollama)", "AI 조언 (Ollama)");
    public static string SetupTemp => T("Detailed temperature sensors", "정밀 온도 센서");
    public static string SetupTempText => T(
        "/status uses a driver-free sensor that many desktops don't expose, so temperatures may be blank. For full CPU/GPU sensors, run enable-temperature.ps1 as administrator — it loads a driver antivirus flags as vulnerable and adds a Defender exclusion for it.",
        "/status는 드라이버 없는 센서를 쓰는데 많은 데스크톱이 값을 주지 않아 온도가 비어 있을 수 있습니다. CPU/GPU 센서를 모두 쓰려면 관리자 PowerShell에서 enable-temperature.ps1을 실행하세요. 백신이 취약 드라이버로 표시하는 드라이버를 불러오고 Defender 예외를 등록합니다.");
    public static string SetupGetStarted => T("GET STARTED", "시작하기");
    public static string OllamaReady(string model) => T($"Ready — model '{model}' is installed.", $"준비됨 — '{model}' 모델이 설치되어 있습니다.");
    public static string OllamaModelMissing => T("Server is running, but the model isn't pulled yet:", "서버는 실행 중이지만 모델을 아직 받지 않았습니다:");
    public static string OllamaNotRunning => T("Installed but not running. Start it to enable AI advice.", "설치됐지만 실행 중이 아닙니다. 시작하면 AI 조언을 쓸 수 있습니다.");
    public static string OllamaNotInstalled => T("Not detected. Install Ollama to enable /advise and automatic health advice.", "찾지 못했습니다. Ollama를 설치하면 /advise와 자동 점검 조언을 쓸 수 있습니다.");
    public static string OllamaGet => T("Get Ollama", "Ollama 받기");
    public static string OllamaStart => T("START OLLAMA", "Ollama 시작");
    public static string OllamaPull => T("RUN PULL", "모델 받기");
    public static string OllamaStarting => T("Starting Ollama…", "Ollama를 시작하는 중…");
    public static string OllamaBadModel => T("Model name has unexpected characters — pull it manually.", "모델 이름에 예상하지 못한 문자가 있습니다 — 직접 받으세요.");
    public static string OllamaPullFailed(string m) => T($"Couldn't start the pull: {m}", $"모델 받기를 시작하지 못했습니다: {m}");

    // ── Test message (ConnectionReadiness) ───────────────────────────────────
    public static string TestNoToken => T("Bot token is not configured yet.", "봇 토큰이 아직 설정되지 않았습니다.");
    public static string TestNoChat => T("No Telegram chat ID is configured yet.", "텔레그램 채팅 ID가 아직 설정되지 않았습니다.");
    public static string TestMessageBody => T("✅ Homebase test — your PC is connected to Telegram.", "✅ Homebase 테스트 — 이 PC가 텔레그램에 연결되어 있습니다.");
    public static string TestSent(long chatId) => T($"Sent to chat {chatId}. Check Telegram.", $"채팅 {chatId}로 보냈습니다. 텔레그램을 확인하세요.");
    public static string TestSentVia(string channel) => channel == "unifiedpush"
        ? T("Sent to your phone (Homebase app push). Check the phone.", "휴대폰(Homebase 앱 푸시)으로 보냈습니다. 휴대폰을 확인하세요.")
        : channel == "telegram"
            ? T("Sent through Telegram. Check Telegram.", "텔레그램으로 보냈습니다. 텔레그램을 확인하세요.")
            : T($"Sent through {channel}.", $"{channel}(으)로 보냈습니다.");
    public static string TestFailed(string m) => T($"Send failed: {m}", $"보내지 못했습니다: {m}");

    // ── Settings › Phone tab (static labels) ─────────────────────────────────
    public static string PhoneHeader => T("CONNECT PHONE", "휴대폰 연결");
    public static string PhoneNewCode => T("NEW CODE", "새 코드");
    public static string PhonePaired => T("PAIRED PHONES", "연결된 휴대폰");
    public static string PhoneRevoke => T("REVOKE", "연결 해제");
    public static string PhonePaths => T("CONNECTION PATHS", "연결 경로");
    public static string PhonePathSettings => T("PATH SETTINGS", "경로 설정");
    public static string PhoneApplyNote => T(
        "Turning this on or changing paths asks for administrator approval, then the Homebase service restarts.",
        "켜거나 경로를 바꾸면 관리자 승인 후 Homebase 서비스가 다시 시작됩니다.");

    // ── Updates (UpdateCoordinator / progress window) ────────────────────────
    public static string UpdChecking => T("Checking…", "확인하는 중…");
    public static string UpdCheckFailed => T("Update check failed", "업데이트 확인 실패");
    public static string UpdErrorTitle => T("Update error", "업데이트 오류");
    public static string UpdCheckFailedDetail(string m) => T($"Update check failed: {m}", $"업데이트를 확인하지 못했습니다: {m}");
    public static string UpdUpToDate(string v) => T($"v{v} — up to date", $"v{v} — 최신 버전");
    public static string UpdUpToDateTitle => T("Update check", "업데이트 확인");
    public static string UpdUpToDateDetail(string v) => T($"Homebase is up to date (v{v}).", $"Homebase가 최신 버전입니다(v{v}).");
    public static string UpdAvailableShort(object v) => T($"v{v} available", $"새 버전 v{v}");
    public static string UpdDlgHeader => T("UPDATE AVAILABLE", "새 버전");
    public static string UpdDlgTitle(string v) => T($"UPDATE AVAILABLE — V{v}", $"새 버전 — v{v}");
    public static string UpdDlgMeta(string installed, DateTimeOffset released) => T(
        $"Installed v{installed}  ·  Released {released:yyyy-MM-dd}",
        $"설치된 버전 v{installed}  ·  배포 {released:yyyy-MM-dd}");
    public static string UpdDlgNoNotes => T("No release notes were published for this version.", "이 버전에는 릴리스 노트가 없습니다.");
    public static string UpdDlgSkip => T("SKIP THIS VERSION", "이 버전 건너뛰기");
    public static string UpdDlgLater => T("LATER", "나중에");
    public static string UpdDlgUpdate => T("UPDATE NOW", "지금 업데이트");
    public static string UpdDlgUpdateCountdown(int seconds) => T($"UPDATE NOW ({seconds})", $"지금 업데이트 ({seconds})");
    public static string UpdDlgCountdownTip => T(
        "Installs automatically when the countdown ends. Press Later or Skip to stop it.",
        "카운트다운이 끝나면 자동으로 설치합니다. 멈추려면 나중에 또는 건너뛰기를 누르세요.");
    public static string UpdSkipped(object v) => T($"v{v} available (skipped)", $"새 버전 v{v} (건너뜀)");
    public static string UpdShowFailed(string m) => T($"Couldn't show the update: {m}", $"업데이트 창을 띄우지 못했습니다: {m}");
    public static string UpdDownloading => T("Downloading the update…", "업데이트를 받는 중…");
    public static string UpdDownloadingShort => T("Downloading update…", "업데이트 받는 중…");
    public static string UpdVerifying => T("Verifying…", "확인하는 중…");
    public static string UpdFailed => T("Update failed", "업데이트 실패");
    public static string UpdDownloadFailed(string m) => T($"Download failed: {m}", $"받지 못했습니다: {m}");
    public static string UpdInstalling => T("Installing… approve the administrator prompt.", "설치하는 중… 관리자 승인 창에서 허용하세요.");
    public static string UpdInstallingShort => T("Installing update…", "업데이트 설치 중…");
    public static string UpdLaunchFailed(string m) => T($"Couldn't launch the installer: {m}", $"설치 프로그램을 실행하지 못했습니다: {m}");
    public static string UpdCancelledTitle => T("Update cancelled", "업데이트 취소");
    public static string UpdCancelledDetail => T(
        "Installing the update needs administrator approval. Nothing was changed — you can try again anytime.",
        "업데이트를 설치하려면 관리자 승인이 필요합니다. 바뀐 것은 없으니 언제든 다시 시도하세요.");
    public static string UpdProgressTitle => T("Homebase Update", "Homebase 업데이트");
    public static string UpdProgressHeader => T("SOFTWARE UPDATE", "소프트웨어 업데이트");
    public static string UpdPreparing => T("Preparing…", "준비하는 중…");
    public static string CancelUpper => T("CANCEL", "취소");
}
