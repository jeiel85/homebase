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
    public static string MenuPhoneWatching => T("● Another device is viewing this PC", "● 다른 기기가 이 PC를 보는 중");
    public static string TrayTipWatching => T("Homebase — another device is viewing this PC", "Homebase — 다른 기기가 이 PC를 보는 중");

    // ── Clipboard (DECISIONS M-62) ───────────────────────────────────────────
    public static string MenuSendClipboard => T("Send clipboard to", "클립보드 보내기");
    public static string ClipLoading => T("Looking for devices…", "기기를 찾는 중…");
    public static string ClipAgentUnavailable => T("The Homebase service isn't reachable. Make sure it is running and up to date.",
        "Homebase 서비스에 닿지 않습니다. 서비스가 실행 중이고 최신 버전인지 확인하세요.");
    public static string ClipNoTargets => T("No device to send to — connect a phone (with the latest app) or another PC",
        "보낼 기기가 없습니다 — 휴대폰(최신 앱)이나 다른 PC를 연결하세요");
    public static string ClipTargetPhone(string name) => T($"{name} (phone)", $"{name} (휴대폰)");
    public static string ClipTargetPc(string name) => T($"{name} (PC)", $"{name} (PC)");
    public static string ClipSendAll(int count) => T($"All {count} devices", $"모든 기기 {count}대");
    public static string ClipSentTitle => T("Clipboard sent", "클립보드를 보냈습니다");
    public static string ClipSentBody(string names) => T($"To {names}.", $"{names}(으)로 보냈습니다.");
    public static string ClipNotSentTitle => T("Clipboard not sent", "클립보드를 보내지 못했습니다");
    public static string ClipPartlySentTitle => T("Clipboard sent to some devices only", "일부 기기에만 보냈습니다");
    public static string ClipBusy => T("Another app is holding the clipboard. Try again in a moment.",
        "다른 앱이 클립보드를 쓰고 있습니다. 잠시 뒤 다시 하세요.");
    public static string ClipNothing => T("The clipboard has no text. Copy some text first.",
        "클립보드에 텍스트가 없습니다. 텍스트를 먼저 복사하세요.");
    public static string ClipTooLong(int max) => T($"The text is too long (up to {max:N0} characters).",
        $"텍스트가 너무 깁니다 (최대 {max:N0}자).");
    public static string ClipSensitiveTitle => T("Send a password?", "비밀번호를 보낼까요?");
    public static string ClipSensitiveBody => T(
        "A password manager copied what is on the clipboard. Send it anyway? It is encrypted on the way, and the other device keeps it out of clipboard history.",
        "클립보드 내용은 비밀번호 관리자가 복사한 것입니다. 그래도 보낼까요? 보내는 동안 암호화되고, 받는 기기는 클립보드 기록에 남기지 않습니다.");
    public static string ClipSendAnyway => T("Send", "보내기");
    public static string ClipReceivedTitle => T("Copied to the clipboard", "클립보드에 넣었습니다");
    public static string ClipReceivedBody(string from, int chars) => T(
        $"Text from {from} ({chars:N0} characters). Paste it with Ctrl+V.",
        $"{from}에서 받은 텍스트({chars:N0}자)입니다. Ctrl+V로 붙여 넣으세요.");
    public static string ClipReceiveFailedTitle => T("Couldn't copy received text", "받은 텍스트를 넣지 못했습니다");
    public static string ClipReceiveFailedBody(string from) => T(
        $"{from} sent text, but another app was holding the clipboard. {from} was told to send it again.",
        $"{from}에서 텍스트를 보냈지만 다른 앱이 클립보드를 쓰고 있었습니다. 보낸 쪽에 다시 보내라고 알렸습니다.");
    // ── Quick send from a phone's PC shortcut (DECISIONS M-78) ───────────────
    public static string QuickHeldTitle(string from) => T($"Text from {from} — not on the clipboard", $"{from}에서 받은 텍스트 — 클립보드에 넣지 않음");
    public static string QuickHeldBody(int chars) => T(
        $"{chars:N0} characters, outside this PC's quick-receive range, so the clipboard was left alone. Click to copy it.",
        $"{chars:N0}자, 받기 범위 밖이라 클립보드를 건드리지 않았습니다. 누르면 클립보드에 넣습니다.");
    public static string QuickHeldNotKeptBody(int chars) => T(
        $"{chars:N0} characters, outside this PC's quick-receive range, and it couldn't be kept here. The phone was told to send it again.",
        $"{chars:N0}자, 받기 범위 밖인데 이 PC에 보관하지 못했습니다. 휴대폰에 다시 보내라고 알렸습니다.");
    public static string QuickHeldSensitiveBody => T(
        "It is outside this PC's quick-receive range and marked private, so it was neither put on the clipboard nor kept. Send it from the share screen instead.",
        "받기 범위 밖이고 민감한 내용이라 클립보드에 넣지도, 보관하지도 않았습니다. 공유 화면에서 다시 보내세요.");
    public static string QuickArchiverStoppedTitle => T("Threads Archiver isn't running", "Threads Archiver가 꺼져 있습니다");
    public static string QuickArchiverStoppedBody(string from) => T(
        $"A Threads link from {from} is on the clipboard, but Threads Archiver isn't running to download it.",
        $"{from}에서 받은 Threads 링크를 클립보드에 넣었지만, Threads Archiver가 꺼져 있어 다운로드되지 않습니다.");
    public static string QuickSection => T("QUICK SEND FROM A PHONE", "휴대폰에서 바로 보낸 것");
    public static string QuickWhat => T(
        "When the phone app sends from a PC shortcut in the share menu without its share screen, only text in this range goes straight onto the clipboard here — with the two link ranges, the link alone, not the text around it. The rest shows a notification and is copied when you click it. Text sent from the share screen is always copied.",
        "휴대폰 앱이 공유 메뉴의 PC 바로가기로 공유 화면 없이 보낸 것 가운데, 이 범위에 드는 것만 바로 클립보드에 넣습니다. 링크 범위 두 가지는 주변 글 없이 링크 하나만 넣습니다. 나머지는 알림만 띄우고, 누르면 넣습니다. 공유 화면에서 보낸 것은 지금처럼 늘 바로 넣습니다.");
    public static string QuickScopeAll => T("All text", "모든 텍스트");
    public static string QuickScopeLinks => T("Only text with a link", "링크가 있는 것만");
    public static string QuickScopeSites => T("Only links to these sites", "지정한 사이트의 링크만");
    public static string QuickSitesHint => T("One site per line; subdomains count too (threads.com covers www.threads.com).",
        "한 줄에 하나, 하위 도메인도 포함합니다(threads.com이면 www.threads.com도).");
    public static string QuickSave => T("Save", "저장");
    public static string QuickSaved => T("Saved.", "저장했습니다.");
    public static string QuickSitesEmpty => T("Add at least one site, or pick another range.", "사이트를 하나 이상 적거나 다른 범위를 고르세요.");
    public static string QuickLineHead => T("Quick receive from phones", "휴대폰 바로 받기");
    public static string QuickLineScope(Services.Mobile.QuickScope scope, string? firstSite, int sites) => scope switch
    {
        Services.Mobile.QuickScope.All => T("all text", "모든 텍스트"),
        Services.Mobile.QuickScope.Links => T("text with a link", "링크가 있는 것만"),
        _ => sites <= 1 ? T($"only {firstSite}", $"지정한 사이트만 ({firstSite})")
            : T($"only listed sites ({firstSite} and {sites - 1} more)", $"지정한 사이트만 ({firstSite} 외 {sites - 1})"),
    };
    public static string QuickLineArchiver(bool running) => running
        ? T("Archiver running", "Archiver 실행 중")
        : T("Archiver isn't running — Threads links received won't be downloaded", "Archiver가 꺼져 있어 받은 Threads 링크가 다운로드되지 않습니다");
    public static string QuickLineToday(int n) => T($"today {n}", $"오늘 {n}건");
    public static string QuickLineLast(DateTimeOffset at) => T($"last {at.ToLocalTime():HH:mm}", $"마지막 {at.ToLocalTime():HH:mm}");
    // ── Received files (DECISIONS M-64) ──────────────────────────────────────
    public static string FilesReceivedTitle(string from, int count, long bytes) => T(
        $"{count} file{(count == 1 ? "" : "s")} from {from} ({Size(bytes)})",
        $"{from}에서 받은 파일 {count}개 ({Size(bytes)})");
    public static string FilesReceivedBody(IReadOnlyList<string> names, bool pictureOnClipboard, bool all)
    {
        var shown = string.Join(", ", names.Take(3)) + (names.Count > 3 ? T($" and {names.Count - 3} more", $" 외 {names.Count - 3}개") : "");
        var text = T($"Saved in Downloads\\Homebase: {shown}.", $"다운로드\\Homebase에 저장했습니다: {shown}.");
        if (pictureOnClipboard) text += T(" The picture is on the clipboard too.", " 사진은 클립보드에도 넣었습니다.");
        if (!all) text += T(" Some files could not be saved.", " 일부 파일은 저장하지 못했습니다.");
        return text;
    }
    // ── Sending files (DECISIONS M-65) ───────────────────────────────────────
    public static string MenuSendFiles => T("Send files to", "파일 보내기");
    public static string FilesPickTitle => T("Files to send", "보낼 파일 고르기");
    public static string FilesNoTargets => T("No device takes files yet — update the phone app, or connect another PC",
        "파일을 받을 수 있는 기기가 없습니다 — 휴대폰 앱을 업데이트하거나 다른 PC를 연결하세요");
    public static string FilesNothing => T("No files to send (folders can't be sent).", "보낼 파일이 없습니다 (폴더는 보낼 수 없습니다).");
    public static string FilesSendingTitle(int count, long bytes) => T($"Sending {count} file{(count == 1 ? "" : "s")} ({Size(bytes)})",
        $"파일 {count}개 ({Size(bytes)}) 보내는 중");
    public static string FilesSendingBody(string names) => T($"To {names}.", $"{names}(으)로 보냅니다.");
    public static string FilesSentTitle(int count, long bytes) => T($"{count} file{(count == 1 ? "" : "s")} sent ({Size(bytes)})",
        $"파일 {count}개를 보냈습니다 ({Size(bytes)})");
    public static string FilesPartlySentTitle => T("Files sent to some devices only", "일부 기기에만 파일을 보냈습니다");
    public static string FilesNotSentTitle => T("Files not sent", "파일을 보내지 못했습니다");
    public static string FilesSentTo(string name) => T($"{name}: has them", $"{name}: 보냈습니다");
    public static string FilesNotified(string name) => T($"{name}: told — the phone fetches them (within a day)",
        $"{name}: 알렸습니다 — 휴대폰에서 받습니다 (하루 안에)");
    public static string FilesReceivedLine(string name) => T($"{name}: received", $"{name}: 받았습니다");
    public static string FilesReceivedByTitle(string name) => T($"{name} received the files", $"{name}에서 파일을 받았습니다");
    public static string FilesReceivedByBody(int count, long bytes) => T($"{count} file{(count == 1 ? "" : "s")}, {Size(bytes)}.",
        $"파일 {count}개, {Size(bytes)}.");
    public static string FileSendError(string? code) => code switch
    {
        "too_large" => T("too large (up to 20 files, 2 GB each, 4 GB together)", "너무 큽니다 (한 번에 20개, 파일당 2GB, 합계 4GB)"),
        "insufficient_storage" => T("not enough room on the PC", "PC에 저장 공간이 부족합니다"),
        "unreadable" => T("a file can't be read (in use, or no access)", "파일을 읽을 수 없습니다 (사용 중이거나 권한 없음)"),
        "agent" => ClipAgentUnavailable,
        "expired" => T("not fetched within a day", "하루 안에 받지 않았습니다"),
        "invalid_transfer" => T("the device can't take these files", "그 기기가 이 파일을 받을 수 없습니다"),
        _ => ClipError(code),
    };

    // ── Send to, drops, zips (DECISIONS M-66) ──
    public static string SendPickTitle => T("Send to", "보낼 곳");
    public static string SendPickSummary(int count, long bytes, string first) => count == 1
        ? T($"{first} ({Size(bytes)})", $"{first} ({Size(bytes)})")
        : T($"{count} files, {Size(bytes)} — {first} and more", $"파일 {count}개 · {Size(bytes)} — {first} 외");
    public static string SendPickFolders(int folders, int files) => T(
        $"{folders} folder{(folders == 1 ? "" : "s")}{(files > 0 ? $" and {files} file{(files == 1 ? "" : "s")}" : "")} (sent as one zip)",
        $"폴더 {folders}개{(files > 0 ? $"와 파일 {files}개" : "")} (zip 하나로 보냄)");
    public static string SendPickText(string preview) => T($"Text: {preview}", $"텍스트: {preview}");
    public static string SendForwardFailed => T("Homebase didn't answer. Quit Homebase from its tray icon, start it again, then send once more.",
        "Homebase가 응답하지 않습니다. 트레이 아이콘에서 Homebase를 끝내고 다시 시작한 뒤 한 번 더 보내 주세요.");
    public static string ZipAskTitle => T("Send as a zip?", "zip으로 묶어 보낼까요?");
    public static string ZipAskBody(int folders, int files, string name) => folders > 0
        ? T($"Folders go as one zip file: {folders} folder{(folders == 1 ? "" : "s")}{(files > 0 ? $" and {files} file{(files == 1 ? "" : "s")}" : "")} in {name}.",
            $"폴더는 zip 파일 하나로 묶어 보냅니다: 폴더 {folders}개{(files > 0 ? $"와 파일 {files}개" : "")}를 {name}(으)로.")
        : T($"One send takes up to {Core.Mobile.TransferProtocol.MaxFiles} files. Send these {files} as one zip, {name}?",
            $"한 번에 {Core.Mobile.TransferProtocol.MaxFiles}개까지 보낼 수 있습니다. 파일 {files}개를 zip 하나({name})로 묶어 보낼까요?");
    public static string ZipAndSend => T("Zip and send", "묶어서 보내기");
    public static string ZippingTitle => T("Making the zip…", "zip으로 묶는 중…");
    public static string ZipError(string? code) => code switch
    {
        "too_large" => T("too large for one file (up to 2 GB)", "파일 하나로 보내기에 너무 큽니다 (2GB까지)"),
        "empty" => T("nothing in it could be read", "읽을 수 있는 파일이 없습니다"),
        _ => T("the zip couldn't be made (is there room in the temp folder?)", "zip을 만들지 못했습니다 (임시 폴더 공간을 확인하세요)"),
    };
    public static string ZipSkipped(int count) => T($"{count} file{(count == 1 ? "" : "s")} or folder{(count == 1 ? "" : "s")} couldn't be read and were left out.",
        $"읽을 수 없는 파일·폴더 {count}개는 뺐습니다.");
    public static string DropNotHere => T("This device can't take files from here — update its app, or connect it again",
        "이 기기로는 보낼 수 없습니다 — 앱을 업데이트하거나 다시 연결하세요");
    public static string DropHint => T("Drop files, folders or text on a phone or PC to send them",
        "파일·폴더·텍스트를 휴대폰이나 PC에 끌어다 놓으면 보냅니다");
    public static string SendToSection => T("SEND TO MENU", "보내기 메뉴");
    public static string SendToToggle => T("Put Homebase in Explorer's \"Send to\" menu", "탐색기 \"보내기\" 메뉴에 Homebase 넣기");
    public static string SendToHelp => T(
        "Right-click files or a folder › Send to › Homebase, then pick the devices. On Windows 11 it is under \"Show more options\". Folders go as one zip.",
        "파일이나 폴더를 오른쪽 클릭 › 보내기 › Homebase를 누르고 받을 기기를 고릅니다. Windows 11에서는 \"추가 옵션 표시\" 안에 있습니다. 폴더는 zip 하나로 묶어 보냅니다.");
    public static string CenterFilesReceived => T("Received files", "받은 파일");
    public static string CenterFilesSent => T("Sent files", "보낸 파일");
    public static string CenterFilesHead => T("FILES", "파일");
    public static string CenterOpenFolder => T("Open folder", "폴더 열기");
    public static string CenterCommands => T("Remote commands", "원격 명령");
    public static string CenterClipHead => T("CLIPBOARD", "클립보드");
    public static string CenterClipReceived => T("Received clipboard", "받은 클립보드");
    public static string CenterClipSent => T("Sent clipboard", "보낸 클립보드");
    public static string CenterCopy => T("Copy to the clipboard", "클립보드에 복사");
    public static string CenterCopied => T("Copied", "복사했습니다");
    public static string ToastOpen => T("Open", "열기");

    // ── Remote commands (DECISIONS M-69) ──
    public static string MenuCancelRestart => T("Cancel the restart", "다시 시작 취소");
    public static string MenuCancelRestartAt(DateTimeOffset at) => T($"Cancel the restart ({at:HH:mm:ss})", $"다시 시작 취소 ({at:HH:mm:ss})");
    public static string RestartToastTitle(DateTimeOffset at) => T($"This PC restarts at {at:HH:mm:ss}", $"{at:HH:mm:ss}에 다시 시작합니다");
    public static string RestartToastBody(string by) => T($"{by} asked for it. Save your work, or cancel.", $"{by}에서 요청했습니다. 작업을 저장하거나 취소하세요.");
    public static string RestartCancelledTitle => T("Restart cancelled", "다시 시작을 취소했습니다");
    public static string RestartCancelledBody(string by) => T($"Called off by {by}.", $"{by}에서 취소했습니다.");
    public static string CustomCommandRanTitle(string name) => T($"Ran \"{name}\"", $"\"{name}\" 실행함");
    public static string CustomCommandRanBody(string from) => T($"{from} asked for it.", $"{from}에서 요청했습니다.");
    public static string CommandsSection => T("REMOTE COMMANDS", "원격 명령");
    public static string CommandsHelp => T(
        "What phones paired with this PC may ask it to do. Restart and sleep need APPLY (and an administrator's approval); a restart counts down 60 seconds here first, with Cancel.",
        "이 PC와 연결된 휴대폰이 요청할 수 있는 것입니다. 바꾼 뒤 APPLY를 눌러야 적용됩니다(관리자 승인). 다시 시작은 이 PC에 60초 카운트다운과 취소가 먼저 뜹니다.");
    public static string CommandLock => T("Lock and turn off the screen", "잠그기·화면 끄기");
    public static string CommandMedia => T("Media and volume", "미디어·볼륨");
    public static string CommandRestart => T("Restart", "다시 시작");
    public static string CommandSleep => T("Sleep", "절전");
    public static string CommandWake => T("Let phones wake this PC (Wake-on-LAN: also turn it on in the BIOS)", "휴대폰에서 깨우기 허용 (Wake-on-LAN — BIOS에서도 켜야 함)");
    public static string CustomCommandsHead => T("Custom commands (run with your rights)", "사용자 정의 명령 (이 Windows 사용자 권한으로 실행)");
    public static string CustomCommandsNone => T("None yet.", "아직 없습니다.");
    public static string CustomCommandAdd => T("Add…", "추가…");
    public static string CustomCommandRemove => T("Remove", "삭제");
    public static string CustomCommandName => T("Name shown on the phone", "휴대폰에 보일 이름");
    public static string CustomCommandProgram => T("Program", "프로그램");
    public static string CustomCommandArguments => T("Arguments (fixed)", "인자 (고정)");
    public static string CustomCommandFull => T($"Up to {Services.Mobile.CustomCommands.Max} custom commands.", $"사용자 정의 명령은 {Services.Mobile.CustomCommands.Max}개까지입니다.");

    public static string FilesFailedTitle(string from) => T($"Files from {from} not saved", $"{from}에서 받은 파일을 저장하지 못했습니다");
    public static string FilesFailedBody => T("Check the free space in Downloads. They are kept on this PC for a day and saved at the next sign-in.",
        "다운로드 폴더의 여유 공간을 확인하세요. 이 PC에 하루 동안 보관했다가 다음 로그인 때 다시 저장합니다.");

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string ClipError(string? code) => code switch
    {
        "no_user_session" => T("nobody is signed in there", "로그인한 사용자가 없습니다"),
        "unreachable" => T("not reachable", "연결할 수 없습니다"),
        "unsupported" => T("update Homebase there first", "그 PC의 Homebase를 먼저 업데이트하세요"),
        "rejected" => T("no longer connected — connect it again", "연결이 끊겼습니다 — 다시 연결하세요"),
        "push_failed" => T("the push didn't go out (is the phone's ntfy app running?)", "푸시를 보내지 못했습니다 (휴대폰의 ntfy 앱이 켜져 있는지 확인하세요)"),
        "clipboard_busy" => T("its clipboard was busy — send again", "그 PC의 클립보드가 사용 중이었습니다 — 다시 보내세요"),
        "unknown_target" => T("no longer connected", "더 이상 연결되어 있지 않습니다"),
        _ => T("failed", "실패했습니다"),
    };

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
    public static string ChipCheck => T("CHECK", "확인 필요");

    // ── Dashboard ────────────────────────────────────────────────────────────
    // Page tabs in the masthead (DECISIONS M-49); the notifications one is NavNotifications(n).
    public static string NavDevices => T("Devices", "기기");
    public static string NavSettings => T("Settings", "설정");
    public static string DashThisPc => T("THIS PC", "지금 이 PC");
    public static string DashDisks => T("DISKS", "디스크");
    public static string DashConnections => T("CONNECTIONS", "연결");
    public static string DashUptime => T("UPTIME", "가동");
    public static string DashMemory => T("RAM", "메모리");
    public static string DashNetwork => T("NETWORK", "네트워크");
    public static string DashTelegram => T("Telegram", "텔레그램");
    public static string DashForwarding => T("Notification forwarding", "알림 전달");
    public static string DashPhone => T("Phone", "휴대폰");
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
    public static string ForwardingOnAutostartOff => T(
        "On — but Homebase is disabled in Task Manager › Startup apps, so forwarding stops after the next sign-in",
        "켜짐 — 하지만 작업 관리자 › 시작 앱에서 Homebase가 '사용 안 함'이라 다음 로그인부터 알림 전달이 멈춥니다");
    public static string PhoneOn => T("On — pair in Settings › Phone", "켜짐 — 설정 › 휴대폰에서 기기 추가");
    public static string PhoneOff => T("Off — turn on in Settings › Phone", "꺼짐 — 설정 › 휴대폰에서 켜기");

    // ── Settings (the dashboard's Settings tab) ─────────────────────────────
    // Each area: its name, and one line under it in the list.
    public static string TabPhone => T("Phone", "휴대폰");
    public static string TabPhoneSub => T("Pair phones and PCs · paths", "휴대폰·PC 연결 · 연결 경로");
    public static string TabTelegram => T("Telegram", "텔레그램");
    public static string TabTelegramSub => T("Bot token · chat ID", "봇 토큰 · 채팅 ID");
    public static string TabForwarding => T("Notification forwarding", "알림 전달");
    public static string TabForwardingSub => T("This PC's notifications to your phone", "이 PC의 알림을 휴대폰으로");
    public static string TabMonitors => T("Watch list", "감시 대상");
    public static string TabMonitorsSub => T("Processes · services · HTTP · TCP", "프로세스 · 서비스 · HTTP · TCP");
    public static string TabAi => T("AI advice", "AI 조언");
    public static string TabAiSub => T("Local Ollama · model", "로컬 Ollama · 모델 상태");
    public static string TabUpdates => T("Updates", "업데이트");
    public static string TabUpdatesSub => T("Check for updates · about", "업데이트 확인 · 정보");

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
    public static string FwAutostartOff => T(
        "Homebase is disabled in Task Manager › Startup apps, so it won't start at the next sign-in and notifications stop reaching your phone. Set it to Enabled there.",
        "작업 관리자 › 시작 앱에서 Homebase가 '사용 안 함'으로 되어 있어 다음 로그인 때 시작되지 않고, 알림도 휴대폰으로 가지 않습니다. 그곳에서 '사용'으로 바꾸세요.");
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

    public static string AiHeader => T("AI ADVICE (OLLAMA)", "AI 조언 (Ollama)");
    public static string AiWhat => T(
        "A local AI model on this PC (Ollama) explains the PC's state in plain words — ask below, from this PC's screen in the phone app, or with /advise in Telegram. When turned on, error alerts and automatic health advice use it too. The model must be downloaded to this PC first.",
        "이 PC의 로컬 AI(Ollama)가 PC 상태를 쉬운 말로 풀어 줍니다 — 아래 '지금 조언 받기', 휴대폰 앱의 PC 화면, 텔레그램 /advise에서 받습니다. 켜 두면 오류 알림 해석과 자동 점검 조언에도 씁니다. 모델은 이 PC에 먼저 받아 둬야 합니다.");
    public static string AiConfigHint => T(
        $"The server address comes from llmAdvisor.endpoint in the Agent config ({Services.TrayConfig.ConfigPath}). Starting Ollama here also makes it run when you sign in, without administrator approval.",
        $"서버 주소는 Agent 설정 파일({Services.TrayConfig.ConfigPath})의 llmAdvisor.endpoint에서 정합니다. 여기서 Ollama를 시작하면 로그인할 때 자동 실행도 함께 등록되며, 관리자 승인은 필요 없습니다.");
    public static string AiAskHeader => T("GET ADVICE NOW", "지금 조언 받기");
    public static string AiAskWhat => T(
        "Shows this PC's current readings (CPU, memory, disks, temperature, recent alerts) to the local AI and asks what to check. The Homebase app on your phone can ask for the same advice from the PC's screen.",
        "이 PC의 지금 상태(CPU·메모리·디스크·온도·최근 경보)를 로컬 AI에게 보여 주고 무엇을 확인할지 묻습니다. 휴대폰의 Homebase 앱에서도 PC 화면에서 같은 조언을 받을 수 있습니다.");
    public static string AiAsk => T("GET ADVICE", "조언 받기");
    public static string AiAskWorking => T("The AI is writing — the first answer can take a few seconds more while the model loads…",
        "AI가 조언을 쓰는 중 — 처음에는 모델을 불러오느라 몇 초 더 걸릴 수 있습니다…");
    public static string AiAskBasis => T("WHAT THE AI SAW", "AI가 본 값");
    public static string AiAskNoAgent => T("The monitoring service isn't reachable, so advice can't be asked for.",
        "감시 서비스에 연결할 수 없어 조언을 받을 수 없습니다.");
    public static string AiAskOldAgent => T("The monitoring service is older than this tray. Update Homebase to ask for advice here.",
        "감시 서비스가 이 트레이보다 오래된 버전입니다. 여기서 조언을 받으려면 Homebase를 업데이트하세요.");
    public static string AiAskMeta(string model, DateTimeOffset at, bool cached) => T(
        $"Model {model} · written at {at:HH:mm}" + (cached ? " · reused (asked again within a minute)" : ""),
        $"모델 {model} · {at:HH:mm}에 작성" + (cached ? " · 1분 안에 다시 물어 방금 것을 보여 줌" : ""));
    public static string AiAskError(string? code, string? message) => code switch
    {
        Core.Advisor.AdviceErrors.Disabled => T("AI advice is turned off (llmAdvisor.enabled in the Agent config).",
            "AI 조언이 꺼져 있습니다(Agent 설정 파일의 llmAdvisor.enabled)."),
        Core.Advisor.AdviceErrors.Unreachable => T("Ollama isn't answering. Start it above, then ask again.",
            "Ollama가 응답하지 않습니다. 위에서 Ollama를 시작한 뒤 다시 받으세요."),
        Core.Advisor.AdviceErrors.ModelMissing => T("The model isn't downloaded yet. Use RUN PULL above, then ask again.",
            "모델을 아직 받지 않았습니다. 위에서 '모델 받기'를 누른 뒤 다시 받으세요."),
        Core.Advisor.AdviceErrors.Timeout => T("The AI didn't answer in time. Try again in a moment — a smaller model answers faster.",
            "AI가 제시간에 답하지 못했습니다. 잠시 뒤 다시 받으세요 — 작은 모델일수록 빨리 답합니다."),
        _ => T($"Couldn't get advice: {message ?? code ?? "?"}", $"조언을 만들지 못했습니다: {message ?? code ?? "?"}"),
    };

    public static string AiAutoHeader => T("AI ON ITS OWN", "자동 AI 조언");
    public static string AiAutoWhat => T(
        "Besides advice you ask for, the PC can use its local AI by itself. Both are off at first, and both need Ollama running with the model pulled.",
        "직접 묻는 조언 말고도 PC가 스스로 로컬 AI를 쓰게 할 수 있습니다. 둘 다 처음에는 꺼져 있고, Ollama가 실행 중이며 모델을 받아 둬야 동작합니다.");
    public static string AiAutoAdvice => T("Send advice when the PC stays overloaded", "PC가 계속 과부하면 조언 보내기");
    public static string AiAutoAdviceHint(double cpu, double mem, double disk, double temp, int minutes, int cooldown) => T(
        $"When CPU ≥ {cpu:0}%, memory ≥ {mem:0}%, a disk ≥ {disk:0}% used or a temperature ≥ {temp:0}°C lasts {minutes} min or more, AI advice comes to your phone — at most once every {cooldown} min.",
        $"CPU {cpu:0}%·메모리 {mem:0}%·디스크 {disk:0}%·온도 {temp:0}°C 이상이 {minutes}분 넘게 이어지면 AI 조언이 휴대폰으로 옵니다({cooldown}분에 한 번까지).");
    public static string AiInterpret => T("Explain event-log errors with AI", "이벤트 로그 오류 알림에 AI 해석 붙이기");
    public static string AiInterpretHint => T(
        "Each Windows event-log error alert gets a short note on what it means and what to check (up to 3 per check).",
        "Windows 이벤트 로그 오류 알림마다 무슨 뜻이고 무엇을 확인할지 짧게 덧붙입니다(한 번 점검에 3건까지).");
    public static string AiInterpretHintOff => T(
        "Event-log monitoring is off in the Agent config (eventLog.enabled), so there are no alerts to explain.",
        "Agent 설정 파일에서 이벤트 로그 감시가 꺼져 있어(eventLog.enabled) 해석할 알림이 없습니다.");
    public static string AiAutoApply => T("APPLY", "적용");
    public static string AiAutoSaved => T("Saved. The monitoring service is restarting to apply.", "저장했습니다. 감시 서비스가 다시 시작되며 적용됩니다.");

    public static string AiModelHeader => T("CHOOSE AN INSTALLED MODEL", "설치된 모델 선택");
    public static string AiModelLabel => T("Model", "모델");
    public static string AiModelHint => T(
        "Choose a downloaded model and save to use it for advice. Smaller models use less memory. Only installed models appear here.",
        "이미 받아 둔 모델을 고르고 저장하면 조언에 사용합니다. 작은 모델일수록 메모리를 적게 씁니다. 이 목록에는 설치된 모델만 표시됩니다.");
    public static string AiModelNone => T(
        "No installed models yet. Start Ollama if needed, then download a model above. This list refreshes when the download finishes.",
        "아직 설치된 모델이 없습니다. 위에서 Ollama를 실행한 뒤 모델을 받으세요. 다운로드가 끝나면 이 목록에 자동으로 표시됩니다.");
    // Notes after a model name in the picker, for the entry that is the configured model.
    public static string AiModelInUse => T("· in use", "· 지금 사용 중");
    public static string AiModelSetNotPulled => T("· set, not pulled yet", "· 지금 설정 · 아직 받지 않음");
    public static string AiModelSet => T("· set", "· 지금 설정");
    // Same admin-approval wording as the watch list: both save through ElevatedConfig.
    public static string AiModelSaveNote => MonSaveNote;
    public static string AiModelSave => T("SAVE", "저장");
    public static string AiModelInvalid => T(
        "That model name has characters Homebase doesn't accept (only letters, digits and . _ - : /).",
        "이 모델 이름에는 쓸 수 없는 문자가 있습니다(영문·숫자와 . _ - : / 만 가능).");
    public static string AiModelSame => T("That's already the model in use.", "지금 쓰는 모델과 같습니다.");
    public static string AiModelSaving => MonSaving;
    public static string AiModelSaved(string model) => T(
        $"Saved — AI advice now uses '{model}'. The monitoring service is restarting to apply.",
        $"저장했습니다 — 이제 '{model}' 모델로 조언합니다. 감시 서비스가 다시 시작되며 적용됩니다.");
    public static string AiModelNotSaved => MonNotSaved;
    public static string AiModelOverridden(string model) => T(
        $"Saved, but the HOMEBASE__llmAdvisor__model environment variable overrides the config file, so '{model}' is still used.",
        $"저장했지만 환경 변수 HOMEBASE__llmAdvisor__model 이 설정 파일보다 우선해서 아직 '{model}' 모델을 씁니다.");

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
    public static string AboutLine => T(
        "Personal Windows PC monitoring on your phone — live view and end-to-end encrypted alerts. Telegram is optional.",
        "휴대폰으로 보는 개인 Windows PC 모니터링 — 실시간 화면과 종단 암호화 알림. 텔레그램은 선택입니다.");

    // ── Setup checklist (onboarding) ─────────────────────────────────────────
    public static string SetupTitle => T("Homebase — Setup", "Homebase — 처음 설정");
    public static string SetupEyebrow => T("SETUP", "처음 설정");
    public static string SetupIntro => T(
        "Here's what's ready on this PC and what still needs a step. Alerts go to the Homebase app on your phone; Telegram is an optional fallback.",
        "이 PC에서 준비된 것과 아직 남은 것을 확인합니다. 경보와 알림은 휴대폰의 Homebase 앱으로 받고, 텔레그램은 원하면 더하는 대체 채널입니다.");
    public static string SetupRequired => T("REQUIRED", "필수");
    public static string SetupOptional => T("OPTIONAL", "선택");
    public static string SetupService => T("Monitoring service", "감시 서비스");
    public static string SetupServiceRunning => T("Running — monitoring is active.", "실행 중 — 감시하고 있습니다.");
    public static string SetupServiceStopped => T("Installed but stopped. Start the Homebase.Agent service.", "설치됐지만 멈춰 있습니다. Homebase.Agent 서비스를 시작하세요.");
    public static string SetupServiceMissing => T("Not installed. Run the Homebase installer.", "설치되지 않았습니다. Homebase 설치 프로그램을 실행하세요.");
    public static string SetupPhone => T("Phone (Homebase app)", "휴대폰 (Homebase 앱)");
    public static string SetupPhoneOff => T(
        "Not turned on yet. Turn it on in Settings › Phone, then scan the QR code with the Homebase app.",
        "아직 꺼져 있습니다. 설정 › 휴대폰에서 켜고 Homebase 앱으로 QR 코드를 찍으세요.");
    public static string SetupPhoneNoBridge => T(
        "On, but not connected to the monitoring service yet. Check that it's running, then re-check.",
        "켜져 있지만 아직 감시 서비스와 이어지지 않았습니다. 서비스가 실행 중인지 확인하고 다시 확인을 누르세요.");
    public static string SetupPhoneNone => T(
        "On, but no phone is paired yet. Scan the QR code in Settings › Phone with the Homebase app.",
        "켜져 있지만 아직 연결된 휴대폰이 없습니다. 설정 › 휴대폰의 QR 코드를 Homebase 앱으로 찍으세요.");
    public static string SetupPhoneReady(int phones) => T(
        phones == 1 ? "Paired with 1 phone — alerts arrive as end-to-end encrypted push." : $"Paired with {phones} phones — alerts arrive as end-to-end encrypted push.",
        $"휴대폰 {phones}대와 연결됨 — 경보와 알림이 종단 암호화 푸시로 갑니다.");
    public static string SetupPhoneUnknown => T(
        "Couldn't read the paired phones. Re-check in a moment.",
        "연결된 휴대폰을 읽지 못했습니다. 잠시 뒤 다시 확인하세요.");
    public static string SetupConnectPhone => T("CONNECT PHONE", "휴대폰 연결하기");
    public static string SetupManagePhone => T("MANAGE", "관리");
    public static string SetupSendTest => T("SEND TEST ALERT", "테스트 알림 보내기");
    public static string SetupTelegram => T("Telegram (fallback)", "텔레그램 (대체 채널)");
    public static string SetupTelegramOff => T(
        "Not set up — that's fine. Add it to get alerts on Telegram when phone push can't get through, and to use bot commands.",
        "설정하지 않았습니다 — 없어도 됩니다. 휴대폰 푸시가 닿지 않을 때 텔레그램으로 대신 받거나 봇 명령을 쓰고 싶을 때만 더하세요.");
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
    public static string OllamaChooseInstalled => T(
        "The configured model isn't installed. Choose one of the installed models below and save.",
        "현재 설정된 모델은 설치되지 않았습니다. 아래 설치된 모델 중에서 고른 뒤 저장하세요.");
    public static string OllamaDownloadModel => T("Model to download", "받을 모델 이름");
    public static string OllamaDownloadConfiguredHint => T(
        "Download the configured model here. To choose another model, open Settings > AI advice.",
        "설정된 모델을 여기서 받습니다. 다른 모델은 설정 > AI 조언에서 선택할 수 있습니다.");
    public static string OllamaDownloadHint => T(
        "Enter an Ollama model name, then download it here (for example qwen3.5:4b or exaone3.5:2.4b). Downloads can be several GB.",
        "받을 모델 이름을 입력하세요(예: qwen3.5:4b, exaone3.5:2.4b). Homebase 안에서 다운로드하며, 모델에 따라 수 GB를 받을 수 있습니다.");
    public static string OllamaDownloading => T("Downloading and checking model files…", "모델 파일을 다운로드·확인하는 중…");
    public static string OllamaDownloadProgress(double percent, double completedMb, double totalMb) => T(
        $"Downloading file: {percent:0}% · {completedMb:0} / {totalMb:0} MB",
        $"파일 다운로드 중: {percent:0}% · {completedMb:0} / {totalMb:0} MB");
    public static string OllamaDownloadDone => T("Downloaded. Select the installed model below and save.", "다운로드했습니다. 아래 설치된 모델을 선택한 뒤 저장하세요.");
    public static string OllamaDownloadCancelled => T("Download cancelled. You can try again.", "다운로드를 취소했습니다. 다시 받을 수 있습니다.");
    public static string OllamaNotRunning => T("Installed but not running. Start it to enable AI advice.", "설치됐지만 실행 중이 아닙니다. 시작하면 AI 조언을 쓸 수 있습니다.");
    public static string OllamaNotInstalled => T("Not detected. Install Ollama to enable /advise and automatic health advice.", "찾지 못했습니다. Ollama를 설치하면 /advise와 자동 점검 조언을 쓸 수 있습니다.");
    public static string OllamaGet => T("Get Ollama", "Ollama 받기");
    public static string OllamaStart => T("START OLLAMA", "Ollama 시작");
    public static string OllamaPull => T("RUN PULL", "모델 받기");
    public static string OllamaStarting => T("Starting Ollama…", "Ollama를 시작하는 중…");
    public static string OllamaBadModel => T("Model name has unexpected characters — pull it manually.", "모델 이름에 예상하지 못한 문자가 있습니다 — 직접 받으세요.");
    public static string OllamaPullFailed(string m) => T($"Couldn't start the pull: {m}", $"모델 받기를 시작하지 못했습니다: {m}");
    public static string OllamaConfig(string model, string endpoint) => T($"Model {model} · server {endpoint}", $"모델 {model} · 서버 {endpoint}");

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
    public static string PhoneCopyLink => T("COPY LINK", "링크 복사");
    public static string PhoneCopyCode => T("COPY CODE", "코드 복사");
    public static string PhoneCopyCodeDone => T(
        "24-word code copied — paste it on the other PC within the time left.",
        "24단어 코드를 복사했습니다 — 남은 시간 안에 다른 PC에 붙여 넣으세요.");
    public static string PhoneCopyLinkHint => T(
        "To link another PC, paste the link or the 24-word code into \"Link another PC\" on that PC. The code works on the same network or tailnet; the link works everywhere.",
        "다른 PC와 연결하려면 링크나 24단어 코드를 그 PC의 \"다른 PC 연결\"에 붙여 넣으세요. 코드는 같은 네트워크·Tailscale에서, 링크는 어디서나 됩니다.");
    public static string PhoneCopyLinkDone => T("Link copied — paste it on the other PC within the time left.", "링크를 복사했습니다 — 남은 시간 안에 다른 PC에 붙여 넣으세요.");
    public static string PhoneCopyLinkExpired => T("The code expired — press NEW CODE first.", "코드가 만료됐습니다 — 먼저 새 코드를 누르세요.");
    public static string PhoneCopyLinkFailed => T("Couldn't copy to the clipboard. Try again.", "클립보드에 복사하지 못했습니다. 다시 누르세요.");
    public static string PhoneLinkHeader => T("LINK ANOTHER PC", "다른 PC 연결");
    public static string PhoneLinkIntro => T(
        "Paste the link (COPY LINK) or the 24-word code (COPY CODE) from the other PC's Settings › Phone. This PC joins that PC's group: every PC and phone in it connects to every other within a few minutes, and they see each other's status and notifications.",
        "다른 PC의 설정 → 휴대폰에서 받은 링크('링크 복사')나 24단어 코드('코드 복사')를 붙여 넣으세요. 이 PC가 그 PC의 그룹에 들어가고, 몇 분 안에 그룹의 모든 PC·휴대폰이 서로 연결되어 상태와 알림을 주고받습니다.");
    public static string PhoneLinkButton => T("LINK", "연결");
    public static string PhoneLinking => T("Linking… (a code searches the network, up to about 10 seconds)", "연결하는 중… (코드는 네트워크에서 그 PC를 찾느라 10초쯤 걸릴 수 있습니다)");
    public static string PhoneLinked(string name) => T($"Linked to {name}.", $"{name}에 연결했습니다.");
    public static string PhoneLinkFailed => T("Couldn't link.", "연결하지 못했습니다.");
    public static string PhoneUnlink => T("UNLINK", "연결 끊기");
    public static string PhoneUnlinkConfirm => T(
        "Take this PC out of the group? Every PC in the group disconnects from it, and status and notifications stop flowing. To bring it back you need a new link or code.",
        "이 PC를 그룹에서 뺄까요? 그룹의 모든 PC에서 연결이 끊기고 상태·알림 전달이 멈춥니다. 다시 넣으려면 새 링크나 코드가 필요합니다.");
    public static string PhoneRevokeConfirm => T(
        "Take this phone out of the group? Every PC in the group disconnects it. To use it again, scan a QR code on any PC.",
        "이 휴대폰을 그룹에서 뺄까요? 그룹의 모든 PC에서 연결이 해제됩니다. 다시 쓰려면 아무 PC에서나 QR로 다시 연결하세요.");

    // Old entries a reinstalled phone left behind (DECISIONS M-71).
    public static string PhoneStaleTitle(int n) => T(
        n == 1 ? "1 old connection of a phone that connected again" : $"{n} old connections of phones that connected again",
        $"다시 연결한 휴대폰의 예전 연결 {n}개");
    public static string PhoneStaleHint => T(
        "Reinstalling the Homebase app (or switching between the GitHub and Play versions) and connecting again adds the phone anew. The old entry has been quiet since then.",
        "Homebase 앱을 다시 설치하거나 GitHub판과 Play판을 바꿔 연결하면 휴대폰이 새로 추가됩니다. 예전 연결은 그 뒤로 소식이 없습니다.");
    public static string PhoneStaleNote => T(
        "Looks like this phone's connection from before it connected again",
        "다시 연결하기 전의 예전 연결로 보입니다");
    public static string PhoneStaleClean => T("CLEAN UP", "예전 연결 정리");
    // The Devices tab's note (M-72): those entries are left out of the list, and cleaned up in Settings › Phone.
    public static string RailReplaced(int n) => T(
        n == 1 ? "1 old connection of a phone that connected again is hidden." : $"{n} old connections of phones that connected again are hidden.",
        $"다시 연결한 휴대폰의 예전 연결 {n}개를 숨겼습니다.");
    public static string RailReplacedClean => T("Clean up…", "정리…");
    public static string PhoneStaleLastSeen(DateTimeOffset at) => T($"last heard {at:MM-dd HH:mm}", $"마지막 소식 {at:MM-dd HH:mm}");
    public static string PhoneStaleConfirm(string list) => T(
        $"Take these old connections out of the group?\n\n{list}\n\nThe phone you connected again stays. If one of them is actually another phone with the same name that has been off, it needs a QR code to connect again.",
        $"이 예전 연결을 그룹에서 뺄까요?\n\n{list}\n\n다시 연결한 휴대폰은 그대로입니다. 이 중에 이름이 같은 다른 휴대폰이 꺼져 있던 것이라면 그 휴대폰은 QR로 다시 연결해야 합니다.");

    // ── Devices (dashboard and Phone tab; DECISIONS M-27, M-29) ──────────────
    public static string DevNone => T(
        "No other devices yet. Pair a phone or link another PC in Settings › Phone.",
        "아직 다른 기기가 없습니다. 설정 → 휴대폰에서 휴대폰을 연결하거나 다른 PC를 연결하세요.");
    public static string DevFeatureOff => T("Turn on the phone connection to see your other devices here.", "휴대폰 연결을 켜면 여기에 다른 기기가 보입니다.");
    public static string DevKindPc => T("PC", "PC");
    public static string DevKindPhone => T("PHONE", "휴대폰");
    public static string DevOnline => T("online", "온라인");
    public static string DevOffline => T("offline", "오프라인");
    public static string DevNoReport => T("no report yet", "아직 보고 없음");
    public static string DevSeen(string ago) => T($"seen {ago}", $"{ago} 확인");
    public static string DevJustNow => T("just now", "방금");
    public static string DevMinutes(int n) => T($"{n} min ago", $"{n}분 전");
    public static string DevHours(int n) => T($"{n} h ago", $"{n}시간 전");
    public static string DevDays(int n) => T($"{n} d ago", $"{n}일 전");
    public static string DevBattery(int pct, bool charging) => T($"battery {pct}%{(charging ? " (charging)" : "")}", $"배터리 {pct}%{(charging ? " (충전 중)" : "")}");
    public static string DevUptime(TimeSpan t) => T($"up {(int)t.TotalDays}d {t.Hours}h", $"가동 {(int)t.TotalDays}일 {t.Hours}시간");
    public static string DevVia(string hub) => T($"via {hub}", $"{hub} 경유");
    public static string DevHubRejected => T("this link was removed on that PC", "그 PC에서 연결이 해제됨");
    public static string DevHubRefused(string error) => T($"answers, but refuses this PC's status ({error})", $"응답하지만 이 PC의 상태 보고를 거부함({error})");
    public static string DetHubRefused(string error, bool relayWaiting) => T(
        $"That PC answers but refuses this PC's status report ({error}) — often a Homebase version the other doesn't know yet. Update both PCs." +
        (relayWaiting ? " Its notifications are held here until it takes the report, so none are relayed meanwhile." : ""),
        $"그 PC가 응답은 하지만 이 PC의 상태 보고를 거부합니다({error}). 대개 두 PC의 Homebase 버전이 달라서입니다. 두 PC 모두 업데이트하세요." +
        (relayWaiting ? " 보고를 받을 때까지 그 PC의 알림을 여기서 중계하지 않습니다." : ""));
    public static string DevHubConnecting => T("connecting…", "연결하는 중…");
    public static string DevHubPhones(int n) => T($"{n} phone(s) there", $"휴대폰 {n}대 연결됨");

    // ── Device page (dashboard, DECISIONS M-46) ──────────────────────────────
    public static string DashAddDevice => T("+  Connect a phone or PC", "+  휴대폰·PC 연결");
    public static string DashRefreshTip => T(
        "Refresh (F5): try the linked PCs again now and read the list again",
        "새로고침 (F5): 연결된 PC에 지금 다시 연결해 보고 목록을 새로 읽습니다");
    public static string DashRefreshing => T("Refreshing…", "새로고침 중…");
    public static string RailThisPc => T("THIS PC", "이 PC");
    public static string RailPcs => T("PCS", "PC");
    public static string RailPhones => T("PHONES", "휴대폰");
    public static string RoleHub => T("linked from this PC", "이 PC가 연결함");
    public static string RoleLinkedHere => T("linked to this PC", "이 PC에 연결함");
    public static string RoleBoth => T("linked both ways", "서로 연결됨");
    public static string ListBattery(int pct, bool charging) => T($"battery {pct}%{(charging ? " ⚡" : "")}", $"배터리 {pct}%{(charging ? " ⚡" : "")}");
    public static string HeadSelf => T("this PC", "이 PC");
    public static string HeadHub => T("a PC this one linked to", "이 PC가 연결한 PC");
    public static string HeadLinkedHere => T("a PC linked to this one", "이 PC에 연결한 PC");
    public static string HeadBoth => T("a PC linked both ways", "서로 연결된 PC");
    public static string HeadPhone => T("paired with this PC", "이 PC에 연결된 휴대폰");
    public static string ChipPhoneLink => T("Phone connection", "휴대폰 연결");
    public static string ChipOpensSettings => T("Change it in Settings", "설정에서 바꿀 수 있습니다");

    // ── Device aliases (DECISIONS M-50, M-51) ────────────────────────────────
    public static string AliasTitle => T("Alias", "별칭");
    public static string AliasExplain(string name) => T(
        $"A name shown instead of {name} on every phone and PC of the group. Leave it empty to show the original name.",
        $"그룹의 모든 휴대폰·PC에서 {name} 대신 보일 이름입니다. 비워 두면 원래 이름으로 보입니다.");
    public static string AliasGroupNote => T(
        "The name set last applies to the whole group. Other PCs get it at once (or within 2 minutes of coming back on); phones when the PC list refreshes.",
        "마지막으로 정한 이름이 그룹 전체에 적용됩니다. 다른 PC에는 바로(꺼져 있었다면 켜진 뒤 2분 안에), 휴대폰에는 PC 목록을 새로고침할 때 반영됩니다.");
    public static string AliasNotSaved => T(
        "The alias was not saved. Check that the Homebase Agent is running and the phone connection is on in Settings › Phone.",
        "별칭을 저장하지 못했습니다. Homebase Agent가 실행 중이고 설정 › 휴대폰 연결이 켜져 있는지 확인하세요.");
    public static string AliasSave => T("Save", "저장");
    public static string AliasReset => T("Original name", "원래 이름");
    public static string AliasSet => T("Set alias…", "별칭 지정…");
    public static string AliasChange => T("Change alias…", "별칭 바꾸기…");
    public static string AliasClear => T("Show the original name", "원래 이름으로 보기");
    public static string AliasTip => T("Set an alias for this device (F2)", "이 기기의 별칭 지정 (F2)");
    public static string PillThisPc => T("this PC", "이 PC");
    public static string PillLive(string via) => T($"live · {via}", $"실시간 · {via}");
    public static string PillConnecting => T("connecting…", "연결하는 중…");
    public static string PillAsking => T("asking to share…", "공유 요청 중…");
    public static string PillStarting => T("starting…", "시작하는 중…");
    public static string DetCollecting => T("Starting this PC's live readings…", "이 PC의 실시간 값을 모으기 시작했습니다…");
    public static string PillNoAnswer => T("no answer", "응답 없음");
    public static string PillOnline(string ago) => T($"online · reported {ago}", $"온라인 · {ago} 보고");
    public static string PillOffline(string ago) => T($"offline · last seen {ago}", $"오프라인 · {ago} 마지막 보고");
    public static string PillLastReport(string ago) => T($"last report {ago}", $"{ago} 보고");
    public static string ViaShared => T("shared by that PC", "상대 PC가 공유");

    public static string DetMemory => T("RAM", "메모리");
    public static string DetDiskIo => T("DISK I/O", "디스크 I/O");
    public static string DetReceive => T("↓ IN", "받기 ↓");
    public static string DetSend => T("↑ OUT", "보내기 ↑");
    public static string DetPing => T("PING", "핑");
    public static string DetTemp => T("TEMP", "온도");
    public static string DetLoss => T("loss", "손실");
    public static string DetAverage => T("avg", "평균");
    public static string DetBoard => T("board", "보드");
    public static string DetFreeOf(string free, string total) => T($"{free} free of {total}", $"{free} 남음 / {total}");
    public static string DetShared => T("shared", "공유");
    public static string DetPower => T("power", "전력");
    public static string DetFan => T("fan", "팬");
    public static string DetUptime => T("Up", "가동");
    public static string DetUptimeDays(int d, int h) => T($"{d}d {h}h", $"{d}일 {h}시간");
    public static string DetUptimeHours(int h, int m) => T($"{h}h {m}m", $"{h}시간 {m}분");
    public static string DetHoursMinutes(int h, int m) => T($"{h} h {m} min", $"{h}시간 {m}분");
    public static string DetMinutesOnly(int m) => T($"{m} min", $"{m}분");
    public static string DetNoNetPerProcess => T("no per-app network", "앱별 네트워크 없음");
    public static string DetApps => T("Apps", "앱");
    public static string DetProcesses => T("Processes", "프로세스");
    public static string DetWork => T("Work", "작업");
    public static string DetSortBy => T("Sort", "정렬");
    public static string DetName => T("Name", "이름");
    public static string DetDisk => T("Disk", "디스크");
    public static string DetNetwork => T("Network", "네트워크");
    public static string DetNoSession => T(
        "Nobody is signed in on that PC (or its tray isn't running), so there are no windows to show.",
        "그 PC에 로그인한 사용자가 없거나 트레이가 꺼져 있어 창 목록이 없습니다.");
    public static string DetNoApps => T("No apps with windows.", "창이 열린 앱이 없습니다.");
    public static string DetNoWork => T("No work sessions, documents or tabs to show.", "보여 줄 작업(AI 세션·문서·탭)이 없습니다.");
    public static string DetMoreWindows(int n) => T($"+ {n} more windows", $"창 {n}개 더");
    public static string DetExpandHint => T("Click to see its windows and child processes", "눌러서 창과 하위 프로세스 보기");
    public static string DetForeground => T("in front", "앞에 열림");
    public static string WorkAgent => T("AI agent", "AI 에이전트");
    public static string AgentWaiting => T("Waiting for input", "입력 대기");
    public static string AgentBusy => T("Working", "작업 중");
    public static string AgentIdle => T("Idle", "쉬는 중");
    public static string WorkEditor => T("Editor", "편집기");
    public static string WorkDocument => T("Document", "문서");
    public static string WorkBrowser => T("Browser", "브라우저");
    public static string DetReconnecting => T("Reconnecting — showing the last reading.", "다시 연결하는 중입니다. 마지막 값을 보여 줍니다.");
    public static string DetStale(string ago) => T($"It doesn't answer right now. This is the last reading, from {ago}.", $"지금은 응답이 없습니다. {ago}의 마지막 값입니다.");
    public static string DetAskingShare(string name) => T(
        $"Asked {name} to share its live view. It starts within a few seconds once {name} gets the request.",
        $"{name}에 실시간 화면 공유를 요청했습니다. 요청이 닿으면 몇 초 안에 시작됩니다.");
    public static string DetConnectingTo(string name) => T($"Connecting to {name}…", $"{name}에 연결하는 중…");
    public static string DetViaHubOnly(string via) => T(
        $"Seen through {via}, so only its last report shows. Connect it to this PC directly to see more.",
        $"{via}를 거쳐 보이는 기기라 마지막 보고만 보입니다. 이 PC와 직접 연결하면 더 자세히 볼 수 있습니다.");
    public static string DetAgentTooOld => T(
        "This PC's Homebase service is older than the tray, so other PCs can't be shown live. Install the update.",
        "이 PC의 Homebase 서비스가 트레이보다 오래된 버전이라 실시간 화면을 볼 수 없습니다. 업데이트를 설치하세요.");
    public static string DetPcOffline => T(
        "That PC doesn't answer: it is off, asleep or out of reach. Its last report is shown. Refresh (F5) tries again at once.",
        "그 PC가 응답하지 않습니다. 꺼져 있거나 절전 중이거나 네트워크에 닿지 않습니다. 마지막 보고를 보여 줍니다. 새로고침(F5)으로 바로 다시 시도할 수 있습니다.");
    public static string DetPcTooOld(string version) => T(
        $"That PC runs Homebase v{version}. Update it to v0.34.0 or later to see it live here.",
        $"그 PC는 Homebase v{version}입니다. v0.34.0 이상으로 업데이트하면 여기서 실시간으로 볼 수 있습니다.");
    public static string DetNoLiveYet => T("No live view yet — its last report is shown.", "아직 실시간 화면이 없어 마지막 보고를 보여 줍니다.");
    public static string DetSelfFeatureOff => T(
        "Turn on the phone connection (Settings › Phone) to see GPUs, temperatures, ping, apps and processes here — and your other devices.",
        "휴대폰·기기 연결(설정 → 휴대폰)을 켜면 GPU·온도·핑·앱·프로세스와 다른 기기까지 여기서 볼 수 있습니다.");
    public static string DetSelfNoAgent => T(
        "The Homebase service doesn't answer, so only the basics are shown. It may be restarting.",
        "Homebase 서비스가 응답하지 않아 기본 정보만 보입니다. 다시 시작하는 중일 수 있습니다.");

    public static string DetPhoneNoReportYet => T(
        "No report since this PC's service started. Opening the Homebase app on the phone sends one.",
        "이 PC의 서비스가 시작된 뒤 아직 보고가 없습니다. 휴대폰에서 Homebase 앱을 열면 바로 보냅니다.");
    public static string DetPhoneNeverReported => T(
        "No report from this phone yet. Open the Homebase app on it.",
        "이 휴대폰의 보고가 아직 없습니다. 휴대폰에서 Homebase 앱을 열어 보세요.");
    public static string DetPhoneOldApp => T(
        "Update the Homebase app on this phone to v0.34.0 or later to see memory, storage, temperature and more here.",
        "휴대폰의 Homebase 앱을 v0.34.0 이상으로 업데이트하면 메모리·저장공간·온도 등도 여기에 보입니다.");
    public static string DetPhoneCadence => T(
        "Phones report when the Homebase app opens and about once a minute while it is open; when it is closed, about every 30 minutes.",
        "휴대폰은 Homebase 앱을 열 때와 열려 있는 동안 약 1분마다, 닫혀 있으면 약 30분마다 상태를 보냅니다.");
    public static string DetBattery => T("BATTERY", "배터리");
    public static string DetCharging => T("charging", "충전 중");
    public static string DetOnBattery => T("on battery", "배터리 사용");
    public static string DetBatteryTemp => T("Battery temp", "배터리 온도");
    public static string DetBatteryHealth => T("Battery health", "배터리 상태");
    public static string DetPlug => T("Charger", "충전 방식");
    public static string DetUntilFull => T("Until full", "완충까지");
    public static string DetThermal => T("Heat", "발열");
    public static string DetStorage => T("Storage", "저장공간");
    public static string DetSystem => T("System", "시스템");
    public static string DetModel => T("Model", "모델");
    public static string DetPowerSave => T("Power saving", "절전 모드");
    public static string DetDnd => T("Do not disturb", "방해 금지");
    public static string DetForwarding => T("Forwarding", "알림 전달");
    public static string DetForwardingOn => T("sends its notifications to PCs", "휴대폰 알림을 PC로 보냄");
    public static string DetForwardingOff => T("off", "보내지 않음");
    public static string DetAppVersion => T("App", "앱 버전");
    public static string DetPhoneSection => T("PHONE", "휴대폰 상태");
    public static string DetWithThisPc => T("WITH THIS PC", "이 PC와의 연결");
    public static string DetConnection => T("CONNECTION", "연결");
    public static string DetPush => T("Push", "푸시 알림");
    public static string DetPushOn => T("gets this PC's notifications", "이 PC의 알림을 받는 중");
    public static string DetPushOnMuted(string list) => T($"gets them, except {list}", $"받는 중 (끈 종류: {list})");
    public static string DetPushOff => T("not registered — this PC's notifications don't reach it", "등록 안 됨 — 이 PC의 알림이 가지 않습니다");
    public static string DetPairedAt => T("Paired", "연결한 날");
    public static string DetLastReport => T("Last report", "마지막 보고");
    public static string DetSeenThrough => T("Seen through", "거쳐 온 PC");
    public static string DetSignal => T("Signal", "신호");
    public static string DetCellular => T("Mobile data", "모바일 데이터");
    public static string DetEthernet => T("Ethernet", "유선");
    public static string DetNoNetwork => T("No network", "연결 없음");
    public static string ChipOnWord => T("On", "켜짐");
    public static string ChipOffWord => T("Off", "꺼짐");
    public static string Health(string h) => h switch
    {
        "good" => T("Good", "양호"),
        "overheat" => T("Overheating", "과열"),
        "dead" => T("Worn out", "수명 다함"),
        "over_voltage" => T("Over voltage", "과전압"),
        "cold" => T("Cold", "저온"),
        "failure" => T("Fault", "이상"),
        _ => h,
    };
    public static string Plug(string p) => p switch
    {
        "ac" => T("Charger", "충전기"),
        "usb" => "USB",
        "wireless" => T("Wireless", "무선"),
        "dock" => T("Dock", "독"),
        _ => p,
    };
    public static string Thermal(string t) => t switch
    {
        "none" => T("Normal", "정상"),
        "light" => T("Warm", "약간 따뜻함"),
        "moderate" => T("Hot", "뜨거움"),
        "severe" => T("Throttling", "성능 제한 중"),
        _ => T("Critical", "위험"),
    };
    public static string CategoryName(string category) => category switch
    {
        "windows-toast" => T("Windows notifications", "Windows 알림"),
        "device-notification" => T("other devices", "다른 기기 알림"),
        "boot" => T("boot", "부팅"),
        "process_watch" => T("process watch", "프로세스 감시"),
        "service_watch" => T("service watch", "서비스 감시"),
        _ => CenterAlertKind(category),
    };

    // ── Notification center (DECISIONS M-32) ────────────────────────────────
    public static string MenuNotifications(int unread) => unread > 0
        ? T($"Notification center ({Count(unread)})", $"알림 센터 ({Count(unread)})")
        : T("Notification center", "알림 센터");
    // The masthead tab shows the label with the count as a badge; the spoken name carries both.
    public static string NavNotificationsLabel => T("Notifications", "알림 센터");
    public static string NavNotifications(int unread) => unread > 0
        ? T($"Notifications, {Count(unread)} new", $"알림 센터, 새 알림 {Count(unread)}건")
        : NavNotificationsLabel;
    public static string UnreadCount(int n) => Count(n);
    private static string Count(int n) => n > 99 ? "99+" : n.ToString(CultureInfo.InvariantCulture);
    public static string CenterAll => T("Everything", "전체");
    public static string CenterAlerts => T("This PC's alerts", "이 PC 경보");
    public static string CenterFrom => T("SENT FROM", "보낸 기기");
    public static string CenterMore => T("OLDER", "더 보기");
    public static string CenterClear => T("CLEAR ALL", "모두 지우기");
    public static string CenterLoading => T("Loading…", "불러오는 중…");
    public static string CenterToday => T("TODAY", "오늘");
    public static string CenterYesterday => T("YESTERDAY", "어제");
    public static string CenterKindAlert => T("ALERT", "경보");
    public static string CenterThisPc => T("This PC", "이 PC");
    public static string CenterSendFailed => T("not delivered", "전송 실패");
    public static string CenterUnreadTip => T("New since you last looked", "지난번에 본 뒤 새로 온 알림");
    public static string CenterExpandTip => T("Click to show or hide the full text", "눌러서 전체 내용 보기/접기");
    public static string CenterAlertKind(string kind) => kind switch
    {
        "event_log" => T("Event log", "이벤트 로그"),
        "advisor" => T("PC health", "PC 상태 조언"),
        _ when kind.EndsWith("_monitor", StringComparison.Ordinal) => T("Dev server watch", "개발 서버 감시"),
        _ when kind.EndsWith("_watch", StringComparison.Ordinal) => T("Watchdog", "감시 대상"),
        _ => kind,
    };
    public static string CenterFeatureOff => T(
        "The notification center needs the phone connection. Turn it on in Settings › Phone.",
        "알림 센터는 휴대폰·기기 연결이 켜져 있어야 씁니다. 설정 → 휴대폰에서 켜세요.");
    public static string CenterNoAgent => T(
        "Couldn't reach the Homebase service. It may be restarting; this window retries when reopened.",
        "Homebase 감시 서비스에 연결하지 못했습니다. 다시 시작하는 중일 수 있습니다. 창을 다시 열면 다시 시도합니다.");
    public static string CenterFailed => T("Couldn't load the notifications.", "알림을 불러오지 못했습니다.");
    public static string CenterEmpty => T(
        "Nothing yet. Notifications your phones and linked PCs send here, and this PC's alerts, stay here for 30 days.",
        "아직 받은 알림이 없습니다. 휴대폰이나 연결한 PC가 보낸 알림과 이 PC의 경보가 여기에 30일 동안 남습니다.");
    public static string CenterEmptyForwardingOff => T(
        "Nothing yet. To receive your other devices' notifications here, turn on notification forwarding in Settings.",
        "아직 받은 알림이 없습니다. 다른 기기의 알림을 여기서 받으려면 설정에서 '알림 전달'을 켜세요.");
    public static string CenterNoMatch => T("Nothing from this source in what is loaded.", "불러온 알림 중 이 출처의 알림이 없습니다.");
    public static string CenterCount(int shown, bool more) => T(
        $"{shown} shown{(more ? " · older ones below" : "")} · kept 30 days",
        $"{shown}건 표시{(more ? " · 더 오래된 알림 있음" : "")} · 30일 보관");
    public static string CenterClearTitle => T("Clear notifications", "알림 지우기");
    public static string CenterClearMessage => T(
        "Delete every notification received from your other devices? This PC's alerts stay (they are also kept for diagnostics).",
        "다른 기기에서 받은 알림을 모두 지울까요? 이 PC의 경보는 진단용으로도 쓰여 남겨 둡니다.");
    public static string CenterClearConfirm => T("Clear", "지우기");

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
    public static string UpdBusyTitle => T("Update in progress", "업데이트 진행 중");
    public static string UpdBusyDetail => T(
        "An update check is already running — wait a moment for its result.",
        "이미 업데이트를 확인하고 있습니다. 결과가 나올 때까지 잠시 기다리세요.");
    public static string UpdAutoNotApproved(string v, DateTimeOffset at) => T(
        $"The automatic install of v{v} ({at:MM-dd HH:mm}) stopped at the administrator prompt — it was declined or nobody answered. Nothing was changed; press UPDATE NOW to try again.",
        $"{at:MM-dd HH:mm}에 시작한 v{v} 자동 설치가 관리자 승인 창에서 멈췄습니다(거부했거나 아무도 응답하지 않음). 바뀐 것은 없으니 '지금 업데이트'를 눌러 다시 시도하세요.");
    public static string UpdAutoFailed(string v, DateTimeOffset at) => T(
        $"The automatic install of v{v} ({at:MM-dd HH:mm}) failed while downloading or starting the installer. Nothing was changed; press UPDATE NOW to try again.",
        $"{at:MM-dd HH:mm}에 시작한 v{v} 자동 설치가 내려받기나 설치 프로그램 실행 중에 실패했습니다. 바뀐 것은 없으니 '지금 업데이트'를 눌러 다시 시도하세요.");
    public static string UpdAutoNotTaken(string v, DateTimeOffset at) => T(
        $"The automatic install of v{v} ({at:MM-dd HH:mm}) ran, but this version still isn't installed. Details: C:\\ProgramData\\Homebase\\logs\\update.log",
        $"{at:MM-dd HH:mm}에 v{v} 자동 설치를 실행했지만 아직 이 버전이 설치되지 않았습니다. 자세한 내용: C:\\ProgramData\\Homebase\\logs\\update.log");
    public static string UpdProgressTitle => T("Homebase Update", "Homebase 업데이트");
    public static string UpdProgressHeader => T("SOFTWARE UPDATE", "소프트웨어 업데이트");
    public static string UpdPreparing => T("Preparing…", "준비하는 중…");
    public static string CancelUpper => T("CANCEL", "취소");
}
