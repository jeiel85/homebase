using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LocalOpsBot.Core.Updates;
using LocalOpsBot.Tray.Services;
using LocalOpsBot.Tray.Services.Mobile;
using Application = System.Windows.Application;

namespace LocalOpsBot.Tray;

/// <summary>
/// System-tray presence via WinForms NotifyIcon (a code-drawn icon that always renders,
/// unlike the previous Hardcodet setup). Left-click toggles the Homebase dashboard popup;
/// right-click opens a context menu.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _trayIcon;
    private readonly UpdateCoordinator _updates = new();
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _watchingItem;
    private readonly ToolStripSeparator _watchingSeparator;
    private readonly Icon _baseIcon;
    private Icon? _watchingIcon;
    private bool _disposed;
    // The one Homebase window; the notification center and Settings are its pages (DECISIONS M-49).
    private DashboardWindow? _popup;
    private OnboardingWindow? _onboarding;
    private readonly ToolStripMenuItem _centerItem;
    private readonly ToolStripMenuItem _clipItem;
    private readonly ToolStripMenuItem _filesItem;
    /// <summary>Shown while a restart a phone asked for counts down (M-69).</summary>
    private readonly ToolStripMenuItem _cancelRestartItem;
    /// <summary>Sends under way (M-65): what was sent to whom, and which targets were already reported.</summary>
    private readonly Dictionary<string, SendTracking> _sends = new(StringComparer.Ordinal);

    private sealed class SendTracking(int count, long bytes, IReadOnlyDictionary<string, string> names)
    {
        public int Count { get; } = count;
        public long Bytes { get; } = bytes;
        public IReadOnlyDictionary<string, string> Names { get; } = names;
        public Dictionary<string, LocalOpsBot.Core.Mobile.FilesProgress> Latest { get; } = new(StringComparer.Ordinal);
        public bool Announced;
        public HashSet<string> ReceivedShown { get; } = new(StringComparer.Ordinal);
    }
    /// <summary>What a click on the balloon now showing does; null = open the notification center.</summary>
    private Action? _balloonClick;
    /// <summary>Stops the pipe a second <c>Homebase.Tray.exe --send</c> hands its paths to (M-66).</summary>
    private readonly CancellationTokenSource _sendRequests = new();

    /// <summary>The running tray, for drops on the Homebase window (M-66); null in the design preview.</summary>
    internal static TrayIconManager? Current { get; private set; }
    private readonly System.Windows.Threading.DispatcherTimer _unreadTimer;
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer;

    // A release found by the startup check while first-run onboarding was open; offered when it closes.
    private UpdateInfo? _deferredOffer;

    public TrayIconManager()
    {
        _baseIcon = LoadTrayIcon();
        _trayIcon = new NotifyIcon
        {
            Text = "Homebase",
            Icon = _baseIcon,
            Visible = true
        };
        _trayIcon.MouseClick += OnTrayClick;

        var menu = new ContextMenuStrip();
        // Shown only while a phone is viewing this PC (see OnWatchingChanged). Informational, so disabled.
        _watchingItem = new ToolStripMenuItem(L.MenuPhoneWatching) { Enabled = false, Visible = false };
        _watchingSeparator = new ToolStripSeparator { Visible = false };
        menu.Items.Add(_watchingItem);
        menu.Items.Add(_watchingSeparator);
        _cancelRestartItem = new ToolStripMenuItem(L.MenuCancelRestart) { Visible = false, Font = new Font(menu.Font, FontStyle.Bold) };
        _cancelRestartItem.Click += async (_, _) => await CancelRestartAsync();
        menu.Items.Insert(0, _cancelRestartItem);

        // Look (dashboard) → change (settings, phone included), then housekeeping. Labels follow the UI
        // language (see L); "Open Homebase" is bold as the left-click default.
        var openItem = new ToolStripMenuItem(L.MenuOpen) { Font = new Font(menu.Font, FontStyle.Bold) };
        openItem.Click += (_, _) => TogglePopup();
        menu.Items.Add(openItem);

        // Notification center (DECISIONS M-32): what other devices sent here and this PC's alerts.
        _centerItem = new ToolStripMenuItem(L.MenuNotifications(0));
        _centerItem.Click += (_, _) => ShowNotificationCenter();
        menu.Items.Add(_centerItem);

        // Send this PC's clipboard text to a phone or another PC of the group (DECISIONS M-62). The targets
        // are asked for each time the submenu opens; shown only while the phone feature is on.
        _clipItem = new ToolStripMenuItem(L.MenuSendClipboard);
        _clipItem.DropDownItems.Add(new ToolStripMenuItem(L.ClipLoading) { Enabled = false });
        _clipItem.DropDownOpening += async (_, _) => await FillClipTargetsAsync();
        menu.Items.Add(_clipItem);

        // Send files from this PC to a phone or another PC of the group (DECISIONS M-65): pick the device, then the files.
        _filesItem = new ToolStripMenuItem(L.MenuSendFiles);
        _filesItem.DropDownItems.Add(new ToolStripMenuItem(L.ClipLoading) { Enabled = false });
        _filesItem.DropDownOpening += async (_, _) => await FillFileTargetsAsync();
        menu.Items.Add(_filesItem);
        menu.Opening += (_, _) => _clipItem.Visible = _filesItem.Visible = MobileHost.Client is not null;

        var settingsItem = new ToolStripMenuItem(L.MenuSettings);
        settingsItem.Click += (_, _) => ShowSettings(SettingsTab.Phone);
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        _updateItem = new ToolStripMenuItem(L.MenuCheckUpdates(_updates.CurrentVersion));
        _updateItem.Click += async (_, _) => await CheckForUpdatesAsync();
        menu.Items.Add(_updateItem);

        var setupItem = new ToolStripMenuItem(L.MenuSetup);
        setupItem.Click += (_, _) => ShowOnboarding();
        menu.Items.Add(setupItem);

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem(L.MenuExit);
        exitItem.Click += (_, _) =>
        {
            App.IsShuttingDown = true;
            Application.Current.Shutdown();
        };
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;

        // A phone watching this PC turns on per-process sampling and window reports (DECISIONS M-13),
        // so say so where the user always looks: a dot on the tray icon, a tooltip and a menu line
        // (M-22). The bridge raises this on a background thread.
        MobileHost.WatchingChanged += OnWatchingChanged;
        ApplyWatching(MobileHost.IsWatching);
        MobileHost.ToastRequested += OnToastRequested;
        MobileHost.ClipHandler = OnClipReceivedAsync;
        MobileHost.FilesHandler = OnFilesReceivedAsync;
        MobileHost.FilesProgress += OnFilesProgress;
        // Remote commands a phone asks for (M-69): carried out here in the user's session; a restart shows its countdown.
        MobileHost.CommandHandler = OnCommandAsync;
        MobileHost.RestartNotice += OnRestartNotice;

        // Explorer's "Send to › Homebase" (M-66): the shortcut kept in step with this install, zips left over from the
        // last run gone, and the pipe a second "Homebase.Tray.exe --send" hands its paths to.
        Current = this;
        _ = Task.Run(() =>
        {
            SendBundle.CleanLeftovers();
            if (Environment.ProcessPath is { } exe) SendToShortcut.Sync(SendPrefs.SendToMenu, exe);
            // The "files received" toast's app id and links point at this install (M-67).
            ReceivedToast.Prepare();
        });
        var dispatcher = Application.Current.Dispatcher;
        _ = Task.Run(() => SendRequests.ServeAsync(
            paths => dispatcher.BeginInvoke(new Action(() => _ = SendPathsAsync(paths))), _sendRequests.Token));

        // A relayed notification's balloon opens the center; the unread count follows new arrivals
        // and is re-read every minute (this PC's alerts do not reach the Tray as balloons).
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            // A received file's balloon opens its folder (M-64); the others open the notification center.
            var click = _balloonClick;
            _balloonClick = null;
            if (click is not null) click();
            else ShowNotificationCenter();
        };
        NotificationCenter.UnreadChanged += n => _centerItem.Text = L.MenuNotifications(n);
        _unreadTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _unreadTimer.Tick += async (_, _) => await NotificationCenter.RefreshUnreadAsync();
        _unreadTimer.Start();
        _ = Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)); // let the bridge connect first
            await NotificationCenter.RefreshUnreadAsync();
        }));

        // An unattended install that has since landed (or been superseded) is settled: log and forget it.
        UpdateAutoOutcome.Reconcile(_updates.CurrentVersion);
        _ = BackgroundCheckAsync();

        // The tray runs for days; look again once a day, relabelling the menu only (no popup mid-work).
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = DailyCheckInterval };
        _updateTimer.Tick += async (_, _) => await DailyCheckAsync();
        _updateTimer.Start();

        // First launch for this Windows user: walk them through the connection checklist
        // once. Guarded so a window failure can never take down the tray itself; it only
        // ever shows again on demand from the "Setup checklist" menu item.
        try
        {
            if (!OnboardingState.IsCompleted())
                ShowOnboarding();
        }
        catch { /* onboarding is non-essential — never let it break tray startup */ }
    }

    /// <summary>A notification from another device (M-28), shown as a Windows notification of this tray icon.</summary>
    private void OnToastRequested(LocalOpsBot.Core.Mobile.TrayToast toast) =>
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            // The sender under the group's alias for it (M-51); an older Agent sends only the title.
            var heading = toast is { Source: { } source, App: { } app } && DeviceNames.AliasOf(null, toast.SourceKind, source) is { } alias
                ? $"{alias} · {app}"
                : toast.Title;
            // NotifyIcon limits: title 63, text 255 characters; an empty text shows no balloon at all.
            var title = Clip(heading, 63);
            var text = Clip(string.IsNullOrWhiteSpace(toast.Body) ? " " : toast.Body!, 255);
            OwnToasts.Remember(title);
            _balloonClick = null;
            try { _trayIcon.ShowBalloonTip(6000, title, text, ToolTipIcon.None); }
            catch (Exception) { /* the shell refused (focus assist etc.): the phones still get it */ }
            // A center in view adds it itself and marks it read; otherwise count it.
            if (_popup is not { ShowsNotifications: true }) _ = NotificationCenter.RefreshUnreadAsync();
        }));

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// Text another device sent (M-62): onto the clipboard on the UI (STA) thread, then a balloon naming the sender.
    /// The result goes back to the sender, which says "busy, send again" when it is false. The balloon never shows
    /// the text — Windows keeps balloons in its notification list, where anyone at the PC could read it.
    /// </summary>
    private Task<bool> OnClipReceivedAsync(LocalOpsBot.Core.Mobile.TrayClip clip)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || _disposed) return Task.FromResult(false);
        return dispatcher.InvokeAsync(async () =>
        {
            if (_disposed) return false;
            var from = DeviceNames.Show(clip.SourceKind, clip.Source);
            bool ok;
            try { ok = await ClipboardShare.PutAsync(clip.Text, clip.Sensitive); }
            catch (Exception ex)
            {
                // A clipboard failure must not reach the bridge loop: it is reported as "not pasted".
                TrayLog.Warn("Clipboard", $"Putting text from {from} on the clipboard failed: {ex.GetType().Name}");
                ok = false;
            }
            TrayLog.Info("Clipboard", ok
                ? $"Text from {from} ({clip.Text.Length} chars{(clip.Sensitive ? ", sensitive" : "")}) put on the clipboard"
                : $"Text from {from} could not be put on the clipboard (busy)");
            ShowBalloon(ok ? L.ClipReceivedTitle : L.ClipReceiveFailedTitle,
                ok ? L.ClipReceivedBody(from, clip.Text.Length) : L.ClipReceiveFailedBody(from));
            return ok;
        }).Task.Unwrap();
    }

    /// <summary>
    /// Files another device sent (M-64): saved in Downloads\Homebase with the user's rights, a single picture also put on
    /// the clipboard, then a balloon whose click shows them in Explorer. The answer tells the Agent whether to delete its copy.
    /// </summary>
    private async Task<LocalOpsBot.Core.Mobile.TrayTransferSaved> OnFilesReceivedAsync(LocalOpsBot.Core.Mobile.TrayTransfer transfer)
    {
        var result = await FileReceiver.SaveAsync(transfer);
        var from = DeviceNames.Show(transfer.SourceKind, transfer.Source);
        var total = transfer.Files.Sum(f => f.Size);
        TrayLog.Info("Files", $"{result.Paths.Count} of {transfer.Files.Count} file(s) from {from} ({total} bytes) saved in {FileReceiver.Folder()}" +
            (result.Ok ? "" : $"; failed: {result.Error}"));

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !_disposed)
        {
            await dispatcher.InvokeAsync(async () =>
            {
                if (_disposed) return;
                var picture = result.Paths.Count == 1 && transfer.Files.Count == 1 && FileReceiver.IsPicture(transfer.Files[0])
                    && await ClipboardShare.PutImageAsync(result.Paths[0]);
                if (result.Paths.Count == 0)
                {
                    ShowBalloon(L.FilesFailedTitle(from), L.FilesFailedBody);
                    return;
                }
                var first = result.Paths[0];
                var title = L.FilesReceivedTitle(from, result.Paths.Count, total);
                var body = L.FilesReceivedBody(result.Paths.Select(System.IO.Path.GetFileName).ToList()!, picture, result.Ok);
                // A Windows toast above the clock, with the picture and [Open] / [Open folder] (M-67); the balloon if Windows won't.
                OwnToasts.Remember(title);
                var showsPicture = transfer.Files.Count == 1 && FileReceiver.IsPicture(transfer.Files[0]);
                if (!ReceivedToast.Show(title, body, result.Paths, showsPicture))
                    ShowBalloon(title, body, () => OpenInExplorer(first));
            }).Task.Unwrap();
        }
        return new LocalOpsBot.Core.Mobile.TrayTransferSaved(transfer.Id, result.Ok, result.Error);
    }

    private static void OpenInExplorer(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { TrayLog.Warn("Files", $"Opening Explorer failed: {ex.Message}"); }
    }

    /// <summary>The "Send clipboard" submenu: the devices the Agent can reach, asked for when it opens.</summary>
    private async Task FillClipTargetsAsync()
    {
        var items = _clipItem.DropDownItems;
        items.Clear();
        items.Add(new ToolStripMenuItem(L.ClipLoading) { Enabled = false });
        var targets = await ClipboardShare.TargetsAsync();
        if (_disposed) return;
        items.Clear();
        if (targets is null)
        {
            items.Add(new ToolStripMenuItem(L.ClipAgentUnavailable) { Enabled = false });
            return;
        }
        if (targets.Count == 0)
        {
            items.Add(new ToolStripMenuItem(L.ClipNoTargets) { Enabled = false });
            return;
        }
        foreach (var t in targets)
        {
            var target = t;
            items.Add(new ToolStripMenuItem(ClipTargetLabel(target), null, async (_, _) => await SendClipboardAsync(new[] { target })));
        }
        if (targets.Count > 1)
        {
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem(L.ClipSendAll(targets.Count), null, async (_, _) => await SendClipboardAsync(targets)));
        }
    }

    private static string ClipTargetLabel(LocalOpsBot.Core.Mobile.ClipTarget t)
    {
        var isPc = t.Kind == LocalOpsBot.Core.Mobile.DeviceKind.Pc;
        var name = DeviceNames.Show(t.Kind, t.Name, isPc ? t.Key : null);
        return isPc ? L.ClipTargetPc(name) : L.ClipTargetPhone(name);
    }

    /// <summary>Reads the clipboard now and sends its text to <paramref name="targets"/> (M-62).</summary>
    private async Task SendClipboardAsync(IReadOnlyList<LocalOpsBot.Core.Mobile.ClipTarget> targets)
    {
        if (Closing) return;
        var snapshot = await ClipboardShare.ReadAsync();
        if (snapshot is null)
        {
            ShowBalloon(L.ClipNotSentTitle, L.ClipBusy);
            return;
        }
        // Files copied in Explorer, or a picture (a screenshot), go as files (M-65); text as before.
        if (snapshot.Files is { Count: > 0 } copied)
        {
            await SendFilesAsync(FileSender.FromPaths(copied), targets);
            return;
        }
        if (snapshot.Text is null && snapshot.Png is { } png)
        {
            var name = $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            await SendFilesAsync(new[] { new FileSender.Item(name, png.Length, "image/png", () => new System.IO.MemoryStream(png, writable: false)) }, targets);
            return;
        }
        if (snapshot.Text is not { } text)
        {
            ShowBalloon(L.ClipNotSentTitle, L.ClipNothing);
            return;
        }
        if (text.Length > LocalOpsBot.Core.Mobile.ClipProtocol.MaxChars)
        {
            ShowBalloon(L.ClipNotSentTitle, L.ClipTooLong(LocalOpsBot.Core.Mobile.ClipProtocol.MaxChars));
            return;
        }
        // A password manager copied it: send only when the user says so, and marked so the other end hides it.
        if (snapshot.Sensitive && !MessageDialog.Show(L.ClipSensitiveTitle, L.ClipSensitiveBody, L.ClipSendAnyway, L.Cancel))
            return;
        await SendTextToAsync(text, snapshot.Sensitive, targets);
    }

    /// <summary>Text to <paramref name="targets"/> (M-62), and one balloon on how it went.</summary>
    private async Task SendTextToAsync(string text, bool sensitive, IReadOnlyList<LocalOpsBot.Core.Mobile.ClipTarget> targets)
    {
        var result = await ClipboardShare.SendAsync(targets.Select(t => t.Key).ToList(), text, sensitive);
        if (_disposed) return;
        if (result is null)
        {
            ShowBalloon(L.ClipNotSentTitle, L.ClipAgentUnavailable);
            return;
        }
        string Name(LocalOpsBot.Core.Mobile.ClipSendOutcome o) =>
            targets.FirstOrDefault(t => t.Key == o.Key) is { } t ? DeviceNames.Show(t.Kind, t.Name, t.Kind == LocalOpsBot.Core.Mobile.DeviceKind.Pc ? t.Key : null) : o.Name;
        var sent = result.Results.Where(o => o.Ok).Select(Name).ToList();
        var failed = result.Results.Where(o => !o.Ok).ToList();
        TrayLog.Info("Clipboard", $"Sent {text.Length} chars{(sensitive ? " (sensitive)" : "")}: " +
            string.Join(", ", result.Results.Select(o => o.Ok ? $"{o.Name} ok" : $"{o.Name} {o.Error}")));
        if (failed.Count == 0)
            ShowBalloon(L.ClipSentTitle, L.ClipSentBody(string.Join(", ", sent)));
        else
            ShowBalloon(sent.Count == 0 ? L.ClipNotSentTitle : L.ClipPartlySentTitle,
                string.Join("\n", failed.Select(o => $"{Name(o)}: {L.ClipError(o.Error)}")));
    }

    /// <summary>The "Send files" submenu: the devices that take files, asked for when it opens (M-65).</summary>
    private async Task FillFileTargetsAsync()
    {
        var items = _filesItem.DropDownItems;
        items.Clear();
        items.Add(new ToolStripMenuItem(L.ClipLoading) { Enabled = false });
        var targets = await FileSender.TargetsAsync();
        if (_disposed) return;
        items.Clear();
        if (targets is null)
        {
            items.Add(new ToolStripMenuItem(L.ClipAgentUnavailable) { Enabled = false });
            return;
        }
        if (targets.Count == 0)
        {
            items.Add(new ToolStripMenuItem(L.FilesNoTargets) { Enabled = false });
            return;
        }
        foreach (var t in targets)
        {
            var target = t;
            items.Add(new ToolStripMenuItem(ClipTargetLabel(target), null, async (_, _) => await PickAndSendAsync(new[] { target })));
        }
        if (targets.Count > 1)
        {
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem(L.ClipSendAll(targets.Count), null, async (_, _) => await PickAndSendAsync(targets)));
        }
    }

    private async Task PickAndSendAsync(IReadOnlyList<LocalOpsBot.Core.Mobile.ClipTarget> targets)
    {
        if (Closing) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.FilesPickTitle, CheckFileExists = true };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;
        await SendFilesAsync(FileSender.FromPaths(dialog.FileNames), targets);
    }

    /// <summary>
    /// Sends <paramref name="items"/> to the <paramref name="targets"/> that take files (M-65): the bytes go to the Agent
    /// first (the tray reads them, with the user's rights), then the Agent tells phones and uploads to PCs. How each target
    /// fares arrives as files.progress and ends in one balloon.
    /// </summary>
    private async Task SendFilesAsync(IReadOnlyList<FileSender.Item> items, IReadOnlyList<LocalOpsBot.Core.Mobile.ClipTarget> targets,
        string? note = null)
    {
        if (items.Count == 0)
        {
            ShowBalloon(L.FilesNotSentTitle, L.FilesNothing);
            return;
        }
        var allowed = await FileSender.TargetsAsync();
        if (allowed is null)
        {
            ShowBalloon(L.FilesNotSentTitle, L.ClipAgentUnavailable);
            return;
        }
        var keys = allowed.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var usable = targets.Where(t => keys.Contains(t.Key)).ToList();
        if (usable.Count == 0)
        {
            ShowBalloon(L.FilesNotSentTitle, L.FilesNoTargets);
            return;
        }
        var bytes = items.Sum(i => i.Size);
        var names = usable.ToDictionary(t => t.Key,
            t => DeviceNames.Show(t.Kind, t.Name, t.Kind == LocalOpsBot.Core.Mobile.DeviceKind.Pc ? t.Key : null), StringComparer.Ordinal);
        ShowBalloon(L.FilesSendingTitle(items.Count, bytes), L.FilesSendingBody(string.Join(", ", names.Values)) + (note is null ? "" : "\n" + note));

        // Tracked before files.go: delivery starts inside it, and its first (even final) progress may come back at once.
        string? tracked = null;
        var result = await Task.Run(() => FileSender.SendAsync(items, usable.Select(t => t.Key).ToList(), id =>
        {
            tracked = id;
            lock (_sends) _sends[id] = new SendTracking(items.Count, bytes, names);
        }));
        if (_disposed) return;
        TrayLog.Info("Files", $"Send of {items.Count} file(s), {bytes} bytes to {string.Join(", ", names.Values)}: {result.Id ?? result.Error}");
        if (result.Id is null)
        {
            if (tracked is not null) lock (_sends) _sends.Remove(tracked);
            ShowBalloon(L.FilesNotSentTitle, L.FileSendError(result.Error));
        }
    }

    /// <summary>A remote command from a phone (M-69). A custom one says so in a balloon; the others show for themselves.</summary>
    private Task<bool> OnCommandAsync(LocalOpsBot.Core.Mobile.TrayCommand command)
    {
        var ok = CommandRunner.Run(command.Name);
        TrayLog.Info("Commands", $"Remote command {command.Name} from {command.From}: {(ok ? "done" : "failed")}");
        if (ok && LocalOpsBot.Core.Mobile.RemoteCommandNames.IsCustom(command.Name)
            && CustomCommands.Find(command.Name[LocalOpsBot.Core.Mobile.RemoteCommandNames.CustomPrefix.Length..]) is { } custom)
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => ShowBalloon(L.CustomCommandRanTitle(custom.Name), L.CustomCommandRanBody(command.From))));
        return Task.FromResult(ok);
    }

    private void OnRestartNotice(DateTimeOffset? at, string by) =>
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (at is { } when)
            {
                _cancelRestartItem.Text = L.MenuCancelRestartAt(when.ToLocalTime());
                _cancelRestartItem.Visible = true;
                if (!ReceivedToast.ShowRestart(when, by)) ShowBalloon(L.RestartToastTitle(when.ToLocalTime()), L.RestartToastBody(by));
                return;
            }
            _cancelRestartItem.Visible = false;
            ReceivedToast.HideRestart();
            ShowBalloon(L.RestartCancelledTitle, L.RestartCancelledBody(by));
        }));

    private async Task CancelRestartAsync()
    {
        if (!await MobileHost.CancelRestartAsync())
        {
            // The Agent didn't take it (not reachable): Windows' own way still works for this user.
            try
            {
                using var abort = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/a") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException) { }
            _cancelRestartItem.Visible = false;
            ReceivedToast.HideRestart();
        }
    }

    /// <summary>
    /// Started by Explorer's "Send to" with no tray running (M-66): this process became the tray, so wait for the bridge
    /// to reach the Agent (it connects in the background) before asking where to send.
    /// </summary>
    internal async Task SendWhenReadyAsync(IReadOnlyList<string> paths)
    {
        for (var i = 0; i < 60 && MobileHost.Client is { IsConnected: false }; i++) await Task.Delay(250);
        await SendPathsAsync(paths);
    }

    /// <summary>
    /// Files and folders from Explorer's "Send to" or dropped on the Homebase window (M-66). Without
    /// <paramref name="targetKey"/> the user picks the devices first. Folders, or more files than one send takes, go as one
    /// zip — asked first, made in the temp folder with the user's rights, deleted once the Agent has the bytes.
    /// </summary>
    internal async Task SendPathsAsync(IReadOnlyList<string> paths, string? targetKey = null)
    {
        if (Closing) return;
        var selection = SendBundle.Classify(paths);
        if (selection.Files.Count == 0 && selection.Folders.Count == 0)
        {
            ShowBalloon(L.FilesNotSentTitle, L.FilesNothing);
            return;
        }
        var targets = await FileSender.TargetsAsync();
        if (Closing) return;
        if (targets is null)
        {
            ShowBalloon(L.FilesNotSentTitle, L.ClipAgentUnavailable);
            return;
        }
        IReadOnlyList<LocalOpsBot.Core.Mobile.ClipTarget>? picked = targetKey is null
            ? SendPickerWindow.Pick(SendSummary(selection), targets, ClipTargetLabel, L.FilesNoTargets)
            : targets.Where(t => t.Key == targetKey).ToList();
        if (picked is null) return;
        if (picked.Count == 0)
        {
            ShowBalloon(L.FilesNotSentTitle, L.DropNotHere);
            return;
        }

        string? zip = null;
        try
        {
            IReadOnlyList<FileSender.Item> items;
            string? note = null;
            if (selection.NeedsZip)
            {
                var name = SendBundle.ZipName(selection, DateTime.Now);
                if (!MessageDialog.Show(L.ZipAskTitle, L.ZipAskBody(selection.Folders.Count, selection.Files.Count, name), L.ZipAndSend, L.Cancel))
                    return;
                ShowBalloon(L.ZippingTitle, name);
                var path = System.IO.Path.Combine(SendBundle.NewWorkFolder(), name);
                var made = await Task.Run(() => SendBundle.Create(selection, path));
                TrayLog.Info("Files", $"Zip of {selection.Folders.Count} folder(s), {selection.Files.Count} file(s): {made.Error ?? "ok"}, {made.Skipped} skipped");
                if (made.Path is null || Closing)
                {
                    SendBundle.Discard(path);
                    if (made.Path is null) ShowBalloon(L.FilesNotSentTitle, L.ZipError(made.Error));
                    return;
                }
                zip = made.Path;
                if (made.Skipped > 0) note = L.ZipSkipped(made.Skipped);
                items = FileSender.FromPaths(new[] { zip });
            }
            else
            {
                items = FileSender.FromPaths(selection.Files);
            }
            await SendFilesAsync(items, picked, note);
        }
        finally
        {
            // The Agent keeps its own copy by now (files.go comes after every byte is written).
            SendBundle.Discard(zip);
        }
    }

    /// <summary>Text dropped on the Homebase window (M-66): sent as clipboard text (M-62), to that device or the ones picked.</summary>
    internal async Task SendTextAsync(string text, string? targetKey = null)
    {
        if (Closing || string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > LocalOpsBot.Core.Mobile.ClipProtocol.MaxChars)
        {
            ShowBalloon(L.ClipNotSentTitle, L.ClipTooLong(LocalOpsBot.Core.Mobile.ClipProtocol.MaxChars));
            return;
        }
        var targets = await ClipboardShare.TargetsAsync();
        if (Closing) return;
        if (targets is null)
        {
            ShowBalloon(L.ClipNotSentTitle, L.ClipAgentUnavailable);
            return;
        }
        var preview = text.Trim().ReplaceLineEndings(" ");
        IReadOnlyList<LocalOpsBot.Core.Mobile.ClipTarget>? picked = targetKey is null
            ? SendPickerWindow.Pick(L.SendPickText(preview.Length > 60 ? preview[..60] + "…" : preview), targets, ClipTargetLabel, L.ClipNoTargets)
            : targets.Where(t => t.Key == targetKey).ToList();
        if (picked is null) return;
        if (picked.Count == 0)
        {
            ShowBalloon(L.ClipNotSentTitle, L.DropNotHere);
            return;
        }
        await SendTextToAsync(text, sensitive: false, picked);
    }

    private static string SendSummary(SendBundle.Selection s)
    {
        if (s.Folders.Count > 0) return L.SendPickFolders(s.Folders.Count, s.Files.Count);
        long bytes = 0;
        foreach (var file in s.Files)
        {
            try { bytes += new System.IO.FileInfo(file).Length; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        }
        return L.SendPickSummary(s.Files.Count, bytes, System.IO.Path.GetFileName(s.Files[0]));
    }

    /// <summary>
    /// How a send stands for one target (M-65). One balloon once every target got as far as it can on its own (a PC has
    /// the files, a phone was told, or it failed), and a short one when a phone then fetches them.
    /// </summary>
    private void OnFilesProgress(LocalOpsBot.Core.Mobile.FilesProgress progress) =>
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            SendTracking? send;
            lock (_sends) _sends.TryGetValue(progress.Id, out send);
            if (send is null) return;
            send.Latest[progress.Key] = progress;
            var name = send.Names.TryGetValue(progress.Key, out var n) ? n : progress.Name;

            if (send.Announced)
            {
                if (progress.State == LocalOpsBot.Core.Mobile.FileSendStates.Received && send.ReceivedShown.Add(progress.Key))
                    ShowBalloon(L.FilesReceivedByTitle(name), L.FilesReceivedByBody(send.Count, send.Bytes));
                return;
            }
            var settled = send.Names.Keys.All(k => send.Latest.TryGetValue(k, out var p)
                && p.State is not LocalOpsBot.Core.Mobile.FileSendStates.Sending);
            if (!settled) return;
            send.Announced = true;
            var lines = send.Names.Select(kv =>
            {
                var p = send.Latest[kv.Key];
                return p.State switch
                {
                    LocalOpsBot.Core.Mobile.FileSendStates.Sent => L.FilesSentTo(kv.Value),
                    LocalOpsBot.Core.Mobile.FileSendStates.Notified => L.FilesNotified(kv.Value),
                    LocalOpsBot.Core.Mobile.FileSendStates.Received => L.FilesReceivedLine(kv.Value),
                    _ => $"{kv.Value}: {L.FileSendError(p.Error)}",
                };
            }).ToList();
            var failed = send.Names.Keys.Count(k => send.Latest[k].State == LocalOpsBot.Core.Mobile.FileSendStates.Failed);
            var title = failed == 0 ? L.FilesSentTitle(send.Count, send.Bytes)
                : failed == send.Names.Count ? L.FilesNotSentTitle : L.FilesPartlySentTitle;
            ShowBalloon(title, string.Join("\n", lines));
            foreach (var kv in send.Latest.Where(kv => kv.Value.State == LocalOpsBot.Core.Mobile.FileSendStates.Received)) send.ReceivedShown.Add(kv.Key);
            // Kept only while a phone may still fetch them (the Agent keeps them a day).
            if (send.Latest.Values.All(p => p.State is not LocalOpsBot.Core.Mobile.FileSendStates.Notified))
                lock (_sends) _sends.Remove(progress.Id);
        }));

    /// <summary>A balloon of the tray's own (never forwarded to the phones as a Windows notification).</summary>
    private void ShowBalloon(string title, string text, Action? onClick = null)
    {
        if (_disposed) return;
        title = Clip(title, 63);
        OwnToasts.Remember(title);
        _balloonClick = onClick;
        try { _trayIcon.ShowBalloonTip(5000, title, Clip(string.IsNullOrWhiteSpace(text) ? " " : text, 255), ToolTipIcon.None); }
        catch (Exception) { /* the shell refused (focus assist etc.) */ }
    }

    private void OnWatchingChanged(bool on) =>
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => ApplyWatching(on)));

    private void ApplyWatching(bool on)
    {
        if (_disposed) return;
        if (on)
        {
            try { _watchingIcon ??= DrawWatchingIcon(_baseIcon); }
            catch { /* drawing failed: tooltip and menu line still tell the user */ }
        }
        _trayIcon.Icon = on && _watchingIcon is not null ? _watchingIcon : _baseIcon;
        _trayIcon.Text = on ? L.TrayTipWatching : "Homebase";
        _watchingItem.Visible = on;
        _watchingSeparator.Visible = on;
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) TogglePopup();
    }

    private DashboardWindow Dashboard() => _popup ??= new DashboardWindow(_updates);

    /// <summary>
    /// The tray is closing (update, restart, Exit): a click must not open windows, which the shutdown
    /// has closed or will close — showing the closed Homebase window threw, as WinForms' ".NET" dialog.
    /// </summary>
    private bool Closing => _disposed || App.IsShuttingDown;

    private void TogglePopup()
    {
        if (Closing) return;
        var popup = Dashboard();
        // Toggle only when it is already the foreground window; if it is hidden OR just behind
        // another window, bring it to the front instead of hiding it, so a tray click always
        // surfaces the dashboard rather than dismissing an out-of-sight one. It opens on the page
        // it was left on.
        if (popup is { IsVisible: true, IsActive: true }) popup.Hide();
        else popup.ShowNearTray();
    }

    private void ShowPopup()
    {
        if (!Closing) Dashboard().BringForward();
    }

    private void ShowSettings(SettingsTab area)
    {
        if (Closing) return;
        var popup = Dashboard();
        popup.ShowTab(DashTab.Settings, area);
        popup.BringForward();
    }

    private void ShowNotificationCenter()
    {
        if (Closing) return;
        var popup = Dashboard();
        popup.ShowTab(DashTab.Notifications);
        popup.BringForward();
    }

    private void ShowOnboarding()
    {
        if (Closing) return;
        if (_onboarding is { IsVisible: true })
        {
            _onboarding.Activate();
            return;
        }
        _onboarding = new OnboardingWindow();
        // Open the dashboard only after onboarding has fully closed — deferring past the
        // close avoids a focus race with the dashboard's show-near-tray activation.
        _onboarding.GetStartedRequested += () =>
            Application.Current.Dispatcher.BeginInvoke(new Action(ShowPopup));
        _onboarding.OpenPhoneSettingsRequested += () => ShowSettings(SettingsTab.Phone);
        _onboarding.OpenTelegramSettingsRequested += () => ShowSettings(SettingsTab.Telegram);
        _onboarding.Closed += (_, _) =>
        {
            _onboarding = null;
            TrayLog.Info("Onboarding", "Setup checklist closed");
            _ = OfferDeferredUpdateAsync();
        };
        _onboarding.Show();
        _onboarding.Activate();
        TrayLog.Info("Onboarding", OnboardingState.IsCompleted() ? "Setup checklist opened" : "Setup checklist opened (first run)");
    }

    // The tray autostarts at sign-in, often before the network is up — the same race that broke the
    // Agent's boot notification (v0.13.2). Retry only failures that waiting can fix.
    private static readonly TimeSpan[] StartupCheckRetryDelays =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>
    /// Startup update check: if a newer release exists, show the update dialog (release notes +
    /// Update / Skip this version / Later). Startup is the one moment an update-and-restart doesn't
    /// interrupt the user's work, which is why this pops a window instead of only relabelling the menu.
    /// </summary>
    private async Task BackgroundCheckAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(10));

        for (var attempt = 0; ; attempt++)
        {
            UpdateInfo? info;
            try
            {
                info = await _updates.CheckAsync(CancellationToken.None);
            }
            catch (UpdateCheckException ex) when (
                ex.Kind is UpdateCheckErrorKind.Network or UpdateCheckErrorKind.Timeout
                && attempt < StartupCheckRetryDelays.Length)
            {
                await Task.Delay(StartupCheckRetryDelays[attempt]);
                continue;
            }
            catch (Exception ex)
            {
                // Rate limit / API error / retries exhausted: background check is best-effort and the
                // menu item still checks on demand.
                TrayLog.Warn("Update", $"Startup update check failed: {ex.Message}");
                return;
            }

            if (info is null)
            {
                TrayLog.Info("Update", $"Startup check: v{_updates.CurrentVersion} is up to date");
                return;
            }
            _updateItem.Text = L.MenuUpdateAvailable(info.Version);

            // Don't stack the update dialog on top of first-run onboarding; offer it once that closes.
            if (_onboarding is not null)
            {
                _deferredOffer = info;
                TrayLog.Info("Update", $"v{info.Version} found while the setup checklist is open; offering it when it closes");
                return;
            }

            await OfferStartupAsync(info);
            return;
        }
    }

    private async Task OfferStartupAsync(UpdateInfo info)
    {
        var status = new Progress<string>(text => _updateItem.Text = text);
        try
        {
            await _updates.OfferStartupUpdateAsync(info, status);
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Update", $"Showing the update dialog failed: {ex.Message}");
            MessageDialog.Show(L.UpdErrorTitle, L.UpdShowFailed(ex.Message));
        }
    }

    /// <summary>
    /// The startup offer that onboarding held back (issue #10): show it once the checklist closes, after
    /// a short pause so "Get started" can bring up the dashboard first.
    /// </summary>
    private async Task OfferDeferredUpdateAsync()
    {
        if (_deferredOffer is not { } info) return;
        _deferredOffer = null;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (_disposed) return;
        if (_onboarding is not null)
        {
            _deferredOffer = info; // reopened meanwhile: wait for that one to close
            return;
        }
        await OfferStartupAsync(info);
    }

    private static readonly TimeSpan DailyCheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Daily re-check (issue #10). Only the menu label changes: a popup in the middle of the day would
    /// interrupt work, and an update restarts the service. The startup check is the one moment that
    /// pops the dialog; clicking the relabelled item runs the normal interactive flow.
    /// </summary>
    private async Task DailyCheckAsync()
    {
        if (_disposed || _updates.IsBusy) return;
        UpdateInfo? info;
        try
        {
            info = await _updates.CheckAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Update", $"Daily update check failed: {ex.Message}");
            return;
        }
        // A flow that started meanwhile owns the menu label.
        if (_disposed || _updates.IsBusy) return;
        if (info is null)
        {
            TrayLog.Info("Update", $"Daily check: v{_updates.CurrentVersion} is up to date");
            return;
        }
        TrayLog.Info("Update", $"Daily check: v{info.Version} available (menu relabelled)");
        _updateItem.Text = UpdateCoordinator.AvailableLabel(info);
    }

    private async Task CheckForUpdatesAsync()
    {
        // The whole flow (check → confirm → download with a progress window → elevated apply) lives
        // in UpdateCoordinator, shared with the dashboard. Reflect its phase text in the menu item.
        _updateItem.Enabled = false;
        try
        {
            var status = new Progress<string>(text => _updateItem.Text = text);
            await _updates.RunInteractiveAsync(status);
        }
        finally
        {
            _updateItem.Enabled = true;
        }
    }

    // Prefer the shipped multi-resolution .ico (crisp at every DPI); fall back to the
    // code-drawn glyph below if the resource can't be loaded for any reason.
    private static Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Homebase.Tray;component/Resources/homebase.ico");
            using var stream = Application.GetResourceStream(uri)!.Stream;
            return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch
        {
            return DrawTrayIcon();
        }
    }

    // Fallback tray glyph: a small house in Homebase's Nintendo palette (carbon roof,
    // amber body), drawn in code if Resources/homebase.ico can't be loaded.
    private static Icon DrawTrayIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var roof = new SolidBrush(Color.FromArgb(0x21, 0x24, 0x2E)); // carbon
            g.FillPolygon(roof, new[] { new PointF(8f, 1.5f), new PointF(1.5f, 7.5f), new PointF(14.5f, 7.5f) });
            using var body = new SolidBrush(Color.FromArgb(0xEC, 0xAB, 0x37)); // amber
            g.FillRectangle(body, 3.5f, 7.5f, 9f, 7f);
            using var door = new SolidBrush(Color.FromArgb(0x21, 0x24, 0x2E));
            g.FillRectangle(door, 6.5f, 10f, 3f, 4.5f);
        }
        var handle = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    // The tray icon with a green "live" dot in the lower-right corner, drawn over the normal icon at
    // the same size so the house stays recognizable.
    private static Icon DrawWatchingIcon(Icon baseIcon)
    {
        var size = baseIcon.Size;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.DrawIcon(baseIcon, new Rectangle(0, 0, size.Width, size.Height));
            var d = Math.Max(6f, size.Width * 0.5f);
            var rect = new RectangleF(size.Width - d, size.Height - d, d - 0.5f, d - 0.5f);
            using var dot = new SolidBrush(Color.FromArgb(0x2E, 0xCC, 0x71));
            using var ring = new Pen(Color.FromArgb(0x21, 0x24, 0x2E), Math.Max(1f, size.Width / 16f));
            g.FillEllipse(dot, rect);
            g.DrawEllipse(ring, rect);
        }
        var handle = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (Current == this) Current = null;
        _sendRequests.Cancel();
        MobileHost.CommandHandler = null;
        MobileHost.RestartNotice -= OnRestartNotice;
        _unreadTimer.Stop();
        _updateTimer.Stop();
        MobileHost.WatchingChanged -= OnWatchingChanged;
        MobileHost.ToastRequested -= OnToastRequested;
        MobileHost.ClipHandler = null;
        MobileHost.FilesHandler = null;
        MobileHost.FilesProgress -= OnFilesProgress;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _baseIcon.Dispose();
        _watchingIcon?.Dispose();
    }
}
