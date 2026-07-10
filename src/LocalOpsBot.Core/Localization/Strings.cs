using System.Globalization;

namespace LocalOpsBot.Core.Localization;

/// <summary>
/// Localized user-facing bot text. English is the default; Korean is used when the current UI
/// culture is Korean. The Agent sets the culture at startup from the <c>agent:language</c> config
/// value, or the OS display language when that is empty. Universal tokens (CPU, RAM, GB, %, IP,
/// URL, Host, ms, EventId, drive letters, versions, Ollama/LM Studio) are left untranslated on
/// purpose. Emoji prefixes stay in the call sites; members here hold text only.
/// </summary>
public static class Strings
{
    /// <summary>True when the current UI culture is Korean.</summary>
    private static bool Ko => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

    private static string T(string en, string ko) => Ko ? ko : en;

    // ── General ──────────────────────────────────────────────────────────────
    public static string Unknown => T("unknown", "알 수 없음");
    public static string Ok => T("OK", "정상");
    public static string On => T("On", "켜짐");
    public static string Off => T("Off", "꺼짐");
    public static string Yes => T("Yes", "예");
    public static string No => T("No", "아니오");
    public static string ErrorLabel => T("Error", "오류");
    public static string StatusLabel => T("Status", "상태");
    public static string Instances(int n) => T($"{n} instance(s)", $"{n}개");

    public static string UnknownCommand(string name) => T(
        $"Unknown command: /{name}\nUse /help to see available commands.",
        $"알 수 없는 명령: /{name}\n사용 가능한 명령은 /help 를 입력하세요.");

    public static string Unauthorized => T("Unauthorized.", "권한이 없습니다.");

    // ── /help ────────────────────────────────────────────────────────────────
    public static string HelpTitle => T("Homebase Commands", "Homebase 명령");
    public static string HelpTip => T(
        "Tip: Use /mute 1h to silence alerts, /unmute to resume.",
        "팁: /mute 1h 로 알림을 끄고, /unmute 로 다시 켤 수 있어요.");

    /// <summary>Localized one-line description for a command (shown in /help).</summary>
    public static string CommandDescription(string command) => command switch
    {
        "ping" => T("Bot health check", "봇 상태 확인"),
        "status" => T("Full PC status summary", "PC 상태 전체 요약"),
        "disk" => T("Disk usage by drive", "드라이브별 디스크 사용량"),
        "uptime" => T("System uptime", "시스템 가동 시간"),
        "process" => T("Watched process status", "감시 중인 프로세스 상태"),
        "services" => T("Windows service watch status", "Windows 서비스 감시 상태"),
        "events" => T("Recent Windows Event Logs", "최근 Windows 이벤트 로그"),
        "mute" => T("Mute alerts for a duration (e.g., /mute 1h, /mute 30m)", "일정 시간 알림 끄기 (예: /mute 1h, /mute 30m)"),
        "unmute" => T("Re-enable alerts", "알림 다시 켜기"),
        "alerts" => T("Recent alert history", "최근 알림 기록"),
        "dev" => T("HTTP endpoint health status", "HTTP 엔드포인트 상태"),
        "ports" => T("TCP port status", "TCP 포트 상태"),
        "help" => T("List available commands", "사용 가능한 명령 목록"),
        "watch" => T("Combined process and service watch status", "프로세스·서비스 감시 상태 통합"),
        "policy" => T("Show current alert policy settings", "현재 알림 정책 설정 보기"),
        "update" => T("Check for and apply updates", "업데이트 확인 및 적용"),
        "http" => T("Monitored HTTP endpoint status", "모니터링 중인 HTTP 엔드포인트 상태"),
        "llm" => T("Local LLM server status (Ollama, LM Studio)", "로컬 LLM 서버 상태 (Ollama, LM Studio)"),
        "diagnostics" => T("Agent self-diagnostics", "에이전트 자가 진단"),
        "advise" => T("AI health advice for your PC (local LLM)", "PC 상태 AI 조언 (로컬 LLM)"),
        _ => command,
    };

    // ── /status ──────────────────────────────────────────────────────────────
    public static string StatusWord => T("Status", "상태");
    public static string Uptime => T("Uptime", "가동 시간");
    public static string Network => T("Network", "네트워크");
    public static string Online => T("Online", "온라인");
    public static string Offline => T("Offline", "오프라인");
    public static string NoIp => T("no IP", "IP 없음");
    public static string Disk => T("Disk", "디스크");
    public static string Free => T("free", "여유");
    public static string Temperature => T("Temperature", "온도");

    // ── /disk ────────────────────────────────────────────────────────────────
    public static string DiskInfoUnavailable => T("Disk information unavailable.", "디스크 정보를 가져올 수 없습니다.");
    public static string DiskStatusTitle => T("Disk Status", "디스크 상태");
    public static string Used => T("used", "사용");
    public static string FreeLabel => T("Free", "여유");

    // ── /uptime ──────────────────────────────────────────────────────────────
    public static string UptimeUnavailable => T("Uptime unavailable.", "가동 시간 정보를 가져올 수 없습니다.");
    public static string Since => T("Since", "시작 시각");

    // ── /process ─────────────────────────────────────────────────────────────
    public static string NoProcessWatches => T(
        "No process watches configured.\nAdd `processWatches` to config.",
        "설정된 프로세스 감시가 없습니다.\n설정에 `processWatches` 를 추가하세요.");
    public static string ProcessWatchTitle => T("Process Watch Status", "프로세스 감시 상태");
    public static string ProcessLabel => T("Process", "프로세스");
    public static string Running => T("Running", "실행 중");
    public static string Missing => T("Missing", "없음");

    // ── /services ────────────────────────────────────────────────────────────
    public static string NoServiceWatches => T(
        "No service watches configured.\nAdd `serviceWatches` to config.",
        "설정된 서비스 감시가 없습니다.\n설정에 `serviceWatches` 를 추가하세요.");
    public static string ServiceWatchTitle => T("Service Watch Status", "서비스 감시 상태");
    public static string ServiceLabel => T("Service", "서비스");
    public static string Expected => T("expected", "기대값");

    // ── /events ──────────────────────────────────────────────────────────────
    public static string RecentEventsTitle => T("Recent Events", "최근 이벤트");
    public static string NoRecentEvents => T("No recent events found.", "최근 이벤트가 없습니다.");
    public static string ProviderLabel => T("Provider", "공급자");
    public static string TimeLabel => T("Time", "시각");

    // ── /mute · /unmute ──────────────────────────────────────────────────────
    public static string InvalidDuration => T(
        "Invalid duration. Use e.g., /mute 30m, /mute 2h, /mute 1d (max 7d).",
        "잘못된 시간입니다. 예: /mute 30m, /mute 2h, /mute 1d (최대 7d).");
    public static string AlertsMuted(string duration, string until) => T(
        $"Alerts muted for {duration} (until {until}).",
        $"{duration} 동안 알림을 껐습니다 (해제: {until}).");
    public static string AlertsUnmuted => T("Alerts unmuted.", "알림을 다시 켰습니다.");

    // ── /alerts ──────────────────────────────────────────────────────────────
    public static string RecentAlertsTitle => T("Recent Alerts", "최근 알림");
    public static string NoRecentAlerts => T("No recent alerts.", "최근 알림이 없습니다.");

    // ── /dev · /http ─────────────────────────────────────────────────────────
    public static string NoHttpEndpoints => T(
        "No HTTP endpoints configured.\nAdd `developerMonitors.httpEndpoints` to config.",
        "설정된 HTTP 엔드포인트가 없습니다.\n설정에 `developerMonitors.httpEndpoints` 를 추가하세요.");
    public static string DevEndpointTitle => T("Dev Endpoint Status", "개발 엔드포인트 상태");
    public static string HttpStatusTitle => T("HTTP Endpoint Status", "HTTP 엔드포인트 상태");
    public static string Down => T("down", "다운");

    // ── /ports ───────────────────────────────────────────────────────────────
    public static string NoTcpPorts => T(
        "No TCP ports configured.\nAdd `developerMonitors.tcpPorts` to config.",
        "설정된 TCP 포트가 없습니다.\n설정에 `developerMonitors.tcpPorts` 를 추가하세요.");
    public static string TcpPortTitle => T("TCP Port Status", "TCP 포트 상태");
    public static string PortOpen => T("Open", "열림");

    // ── /watch ───────────────────────────────────────────────────────────────
    public static string NoWatches => T(
        "No watches configured.\nAdd `processWatches` or `serviceWatches` to config.",
        "설정된 감시가 없습니다.\n설정에 `processWatches` 또는 `serviceWatches` 를 추가하세요.");
    public static string WatchStatusTitle => T("Watch Status", "감시 상태");
    public static string ProcessWatchesTitle => T("Process Watches", "프로세스 감시");
    public static string ServiceWatchesTitle => T("Service Watches", "서비스 감시");

    // ── /policy ──────────────────────────────────────────────────────────────
    public static string AlertPolicyTitle => T("Alert Policy", "알림 정책");
    public static string NoMuteActive => T("No mute active", "음소거 없음");
    public static string MutedUntilPolicy(string until, string minutes) => T(
        $"Muted until {until} UTC ({minutes} min remaining)",
        $"{until} UTC 까지 음소거 ({minutes}분 남음)");
    public static string MuteStateLabel => T("Mute state", "음소거 상태");
    public static string DedupWindowLabel => T("Dedup window", "중복 제거 창");
    public static string MaxPerMinLabel => T("Max messages/min", "분당 최대 메시지");
    public static string MaxPerHourLabel => T("Max messages/hour", "시간당 최대 메시지");
    public static string RecoveryAlertsLabel => T("Recovery alerts", "복구 알림");
    public static string CriticalBypassLabel => T("Critical bypass mute", "심각 알림 음소거 무시");
    public static string PolicyTip => T(
        "Use /mute 1h to silence, /unmute to resume.",
        "/mute 1h 로 알림을 끄고, /unmute 로 다시 켤 수 있어요.");

    // ── /llm ─────────────────────────────────────────────────────────────────
    public static string LocalLlmTitle => T("Local LLM Status", "로컬 LLM 상태");
    public static string LlmRunning(string ms) => T($"running ({ms}ms)", $"실행 중 ({ms}ms)");
    public static string NotRunning => T("not running", "실행 안 함");

    // ── /update ──────────────────────────────────────────────────────────────
    public static string UpToDateTitle => T("Up-to-date", "최신 버전");
    public static string CurrentVersionLabel => T("Current version", "현재 버전");
    public static string CurrentLabel => T("Current", "현재");
    public static string UpdateFailedTitle => T("Update failed", "업데이트 실패");
    public static string UpdateFailedBody(string version, string message, string current) => T(
        $"v{version} could not be installed: {message}\nThe current version ({current}) is still running.",
        $"v{version} 을(를) 설치하지 못했습니다: {message}\n현재 버전({current})이 계속 실행 중입니다.");
    public static string UpdateDownloadedTitle(string version) => T(
        $"Update v{version} downloaded &amp; verified",
        $"업데이트 v{version} 다운로드 및 검증 완료");
    public static string PublishedLabel => T("Published", "게시일");
    public static string InstallingNow => T(
        "Installing now — the service will restart in a few seconds.",
        "지금 설치 중 — 서비스가 곧 재시작됩니다.");
    public static string UpdateCheckFailedTitle => T("Update check failed", "업데이트 확인 실패");

    // ── /advise ──────────────────────────────────────────────────────────────
    public static string AiAdvisorTitle => T("AI Advisor", "AI 조언");
    public static string AdvisorUnavailable => T("The advisor is unavailable.", "조언 기능을 사용할 수 없습니다.");
    public static string AdvisorSetupHint => T(
        "Make sure a local LLM server (Ollama) is running and the model is pulled. Use /llm to check the server, or run <code>ollama pull llama3.2:1b</code>.",
        "로컬 LLM 서버(Ollama)가 실행 중이고 모델이 받아져 있는지 확인하세요. /llm 으로 서버를 확인하거나 <code>ollama pull llama3.2:1b</code> 를 실행하세요.");

    // ── /diagnostics ─────────────────────────────────────────────────────────
    public static string DiagnosticsTitle => T("Homebase Diagnostics", "Homebase 진단");
    public static string VersionLabel => T("Version", "버전");
    public static string AgentUptimeLabel => T("Agent uptime", "에이전트 가동 시간");
    public static string DatabaseLabel => T("Database", "데이터베이스");
    public static string LastPollLabel => T("Last Telegram poll", "마지막 Telegram 폴링");
    public static string PollFailuresLabel => T("Consecutive poll failures", "연속 폴링 실패");
    public static string MuteLabel => T("Mute", "음소거");
    public static string WatchesLabel => T("Watches", "감시");
    public static string AlertsSent24hLabel => T("Alerts sent (24h)", "발송된 알림 (24h)");
    public static string ForwardingLabel => T("Notification forwarding", "알림 전달");
    public static string TemperatureLabel => T("Temperature", "온도");
    public static string Never => T("never", "없음");
    public static string TimeAgo(string age) => T($"{age} ago", $"{age} 전");
    public static string MuteUntil(string time) => T($"until {time}", $"{time} 까지");
    public static string WatchesSummary(int process, int service, int http, int port) => T(
        $"{process} process, {service} service, {http} http, {port} port",
        $"프로세스 {process}, 서비스 {service}, http {http}, 포트 {port}");

    // ── Alerts: watchdog (process / service) ─────────────────────────────────
    public static string ProcessDown(string name) => T($"Process down: {name}", $"프로세스 중단: {name}");
    public static string ProcessDownBody(int minInstances, string host) => T(
        $"No running instance found (expected ≥{minInstances}). Host: {host}",
        $"실행 중인 인스턴스가 없습니다 (기대 ≥{minInstances}). 호스트: {host}");
    public static string ProcessRecovered(string name) => T($"Process recovered: {name}", $"프로세스 복구: {name}");
    public static string ServiceNotRunning(string name) => T($"Service not running: {name}", $"서비스 실행 안 함: {name}");
    public static string ServiceDownBody(string serviceName, string status, string host) => T(
        $"Service '{serviceName}' status: {status}. Host: {host}",
        $"서비스 '{serviceName}' 상태: {status}. 호스트: {host}");
    public static string ServiceRecovered(string name) => T($"Service recovered: {name}", $"서비스 복구: {name}");

    // ── Alerts: dev monitor (http / tcp) ─────────────────────────────────────
    public static string EndpointDown(string name) => T($"Endpoint down: {name}", $"엔드포인트 중단: {name}");
    public static string EndpointRecovered(string name) => T($"Endpoint recovered: {name}", $"엔드포인트 복구: {name}");
    public static string PortClosed(string name) => T($"Port closed: {name}", $"포트 닫힘: {name}");
    public static string PortRecovered(string name) => T($"Port recovered: {name}", $"포트 복구: {name}");
    public static string ConnectionFailed => T("connection failed", "연결 실패");

    // ── Boot notification ────────────────────────────────────────────────────
    public static string BootDetected(string host) => T($"{host} boot detected", $"{host} 부팅 감지");

    // ── Active health advice alert ───────────────────────────────────────────
    public static string TriggeredBy(string detail) => T($"Triggered by: {detail}", $"발생 원인: {detail}");
    public static string PcHealthAdvice => T("PC health advice", "PC 상태 조언");

    // ── Event log alerts ──────────────────────────────────────────────────────
    public static string EventLevel(string level) => level switch
    {
        "Critical" => T("Critical", "심각"),
        "Error" => T("Error", "오류"),
        "Warning" => T("Warning", "경고"),
        "Information" => T("Information", "정보"),
        _ => level,
    };
    public static string EventAlertTitle(string level, string source) => T(
        $"{level} event: {source}",
        $"{level} 이벤트: {source}");
    public static string EventLogLabel => T("Log", "로그");
    public static string EventIdLabel => T("Event ID", "이벤트 ID");

    // ── /ping ────────────────────────────────────────────────────────────────
    public static string Pong => T("🏓 Pong! Homebase is alive.", "🏓 퐁! Homebase 정상 작동 중.");
}
