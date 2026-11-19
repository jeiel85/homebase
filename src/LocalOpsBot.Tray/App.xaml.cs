using System.Globalization;
using System.Windows;
using LocalOpsBot.Tray.Services;

namespace LocalOpsBot.Tray;

public partial class App : Application
{
    // Single-instance guard (session-local): each signed-in user gets one tray, but a second
    // launch in the same session — autostart racing a manual start, or a stale relaunch after an
    // update — exits immediately instead of stacking a duplicate tray icon and pipe client.
    private const string SingleInstanceMutexName = @"Local\Homebase.Tray.SingleInstance";
    private Mutex? _singleInstanceMutex;

    private TrayIconManager? _trayIcon;
    private NotificationForwardingHost? _forwardingHost;

    /// <summary>
    /// Set just before a programmatic <see cref="Application.Shutdown()"/> (e.g. an auto-update
    /// restart) so windows that normally cancel their close — the dashboard hides instead of
    /// closing — let the shutdown proceed cleanly.
    /// </summary>
    internal static bool IsShuttingDown { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Uninstall: take our Claude Code hook out of one settings.json and exit — no window, no mutex
        // (uninstall-service.ps1, once per user profile). Exit code 0 = done or nothing to do (#64).
        //   Homebase.Tray.exe --remove-claude-hook <path to .claude\settings.json>
        if (ArgAfter(e.Args, "--remove-claude-hook") is { } settingsPath)
        {
            Shutdown(Services.Mobile.ClaudeHookSetup.RemoveFrom(settingsPath) ? 0 : 1);
            return;
        }

        // A click on a "files received" toast (M-67): homebase://files/… opens a received file or the folder, then exits.
        //   Homebase.Tray.exe --toast <link>
        if (ArgAfter(e.Args, Services.Mobile.ReceivedToast.Argument) is { } link)
        {
            Services.Mobile.ReceivedToast.Handle(link);
            Shutdown();
            return;
        }

        // Shows a sample "files received" toast (M-67) for the files given, as if a phone had sent them; nothing is saved:
        //   Homebase.Tray.exe --preview-received-toast <path> [<path> …] [--lang en]
        if (e.Args.Contains("--preview-received-toast"))
        {
            base.OnStartup(e);
            var lang = ArgAfter(e.Args, "--lang");
            if (lang is not null) CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(lang);
            else ApplyLanguage();
            var files = e.Args.SkipWhile(a => a != "--preview-received-toast").Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))
                .Where(System.IO.File.Exists).ToList();
            if (files.Count > 0)
            {
                var total = files.Sum(f => new System.IO.FileInfo(f).Length);
                var shown = Services.Mobile.ReceivedToast.Show(L.FilesReceivedTitle("Galaxy S24", files.Count, total),
                    L.FilesReceivedBody(files.Select(System.IO.Path.GetFileName).ToList()!, false, true), files, picture: true);
                TrayLog.Info("Files", $"Preview toast: shown={shown}, {Services.Mobile.ReceivedToast.Diagnose()}");
            }
            Shutdown();
            return;
        }

        // Design preview of the "Send to" picker (M-66) with made-up devices; nothing is sent:
        //   Homebase.Tray.exe --preview-send-picker [--lang en]
        if (e.Args.Contains("--preview-send-picker"))
        {
            base.OnStartup(e);
            var lang = ArgAfter(e.Args, "--lang");
            if (lang is not null) CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(lang);
            else ApplyLanguage();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var targets = new[]
            {
                new Core.Mobile.ClipTarget("device:preview-s24", "Galaxy S24", Core.Mobile.DeviceKind.Phone),
                new Core.Mobile.ClipTarget("device:preview-tab", "Galaxy Tab S9", Core.Mobile.DeviceKind.Phone),
                new Core.Mobile.ClipTarget("pc:preview-laptop", "LAPTOP-JEIEL", Core.Mobile.DeviceKind.Pc),
            };
            SendPickerWindow.Pick(L.SendPickSummary(3, 12_400_000, "IMG_0412.jpg"), targets,
                t => t.Kind == Core.Mobile.DeviceKind.Pc ? L.ClipTargetPc(t.Name) : L.ClipTargetPhone(t.Name), L.FilesNoTargets,
                remember: false);
            Shutdown();
            return;
        }

        // Design preview of the Homebase window with made-up devices and notifications (no tray icon, no
        // mutex). Settings is the real page: it reads this PC's configuration and, on the Phone area,
        // asks this PC's Agent for a pairing code as the tray does:
        //   Homebase.Tray.exe --preview-dashboard [--view devices|notifications|settings]
        //                     [--area phone|telegram|forwarding|monitors|ai|updates] [--select <device key>] [--lang en]
        //                     [--alias "<group key>=<alias>"] [--rename]   (pc:pc-work, phone-name:galaxy s24|samsung sm-s921n)
        if (e.Args.Contains("--preview-dashboard"))
        {
            base.OnStartup(e);
            var lang = ArgAfter(e.Args, "--lang");
            if (lang is not null) CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(lang);
            else ApplyLanguage();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            Services.Mobile.NotificationCenter.Preview = Dashboard.PreviewDashboardSource.History();
            // Aliases stay in memory: the preview never reaches the Agent or the group (M-51).
            Services.Mobile.DeviceNames.UseMemoryOnly();
            // A phone key carries its name (phone-name:<name>|<model>), so the notification center finds it too.
            if (ArgAfter(e.Args, "--alias")?.Split('=', 2) is [var aliasKey, var alias])
                Services.Mobile.DeviceNames.Aliases.Set(aliasKey,
                    aliasKey.StartsWith("phone-name:", StringComparison.Ordinal) ? aliasKey[11..].Split('|')[0] : aliasKey, alias);
            var preview = new DashboardWindow(new Dashboard.PreviewDashboardSource(), preview: true, new UpdateCoordinator());
            if (Enum.TryParse<DashTab>(ArgAfter(e.Args, "--view"), ignoreCase: true, out var view))
                preview.ShowTab(view, Enum.TryParse<SettingsTab>(ArgAfter(e.Args, "--area"), ignoreCase: true, out var area) ? area : null);
            if (e.Args.Contains("--rename")) preview.ContentRendered += (_, _) => preview.PreviewRename();
            if (ArgAfter(e.Args, "--select") is { } key) preview.PreviewSelect(key);
            if (int.TryParse(ArgAfter(e.Args, "--tab"), out var tab)) Dashboard.PcLiveView.PreviewTab = tab;
            if (ArgAfter(e.Args, "--size")?.Split('x') is [var w, var h] && double.TryParse(w, out var pw) && double.TryParse(h, out var ph))
            {
                preview.Width = pw;
                preview.Height = ph;
            }
            MainWindow = preview;
            preview.Show();
            // Count the made-up history as the tray does, so the Notifications tab shows its badge
            // until the tab is opened.
            _ = Services.Mobile.NotificationCenter.RefreshUnreadAsync();
            return;
        }

        // Explorer's "Send to › Homebase" (DECISIONS M-66): Homebase.Tray.exe --send <paths>.
        var sendPaths = Services.Mobile.SendRequests.PathsFrom(e.Args);

        // Hold the mutex for the process lifetime; createdNew == false means another tray in this
        // session already owns it, so bail out before creating a second icon / pipe client — handing
        // it the paths to send first, when that is why this one was started.
        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            if (sendPaths is { Count: > 0 } && !Services.Mobile.SendRequests.Forward(sendPaths, TimeSpan.FromSeconds(5)))
            {
                ApplyLanguage();
                System.Windows.MessageBox.Show(L.SendForwardFailed, "Homebase", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        LogUnhandledErrors();
        ApplyLanguage();
        _trayIcon = new TrayIconManager();

        // Start toast-notification forwarding if the user has enabled it. This is the tray half of
        // the pipeline (listener → pipe → Agent → Telegram); it stays dormant until enabled.
        _forwardingHost = new NotificationForwardingHost();
        _forwardingHost.StartIfEnabled();

        // Mobile monitoring bridge: connects to the Agent but collects nothing until a phone watches.
        Services.Mobile.MobileHost.StartIfEnabled();

        // Started by "Send to" with no tray running: this one is the tray now, and sends once it reaches the Agent.
        if (sendPaths is { Count: > 0 }) _ = _trayIcon.SendWhenReadyAsync(sendPaths);
    }

    /// <summary>
    /// Use the same language as the bot: <c>agent:language</c> when set, otherwise the OS display
    /// language (the default UI culture). An invalid code is ignored, as the Agent does.
    /// </summary>
    private static void ApplyLanguage()
    {
        try
        {
            var language = TrayConfig.Load()["agent:language"];
            if (string.IsNullOrWhiteSpace(language)) return;
            var culture = CultureInfo.GetCultureInfo(language.Trim());
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (Exception)
        {
            // Unknown culture code or unreadable config: keep the OS display language.
        }
    }

    /// <summary>
    /// Errors nothing caught go to tray.log. One thrown by a tray icon or menu click (WinForms) used to
    /// show WinForms' own ".NET" error dialog (Continue / Quit) and leave no trace; it is now logged and
    /// the tray carries on, as Continue did. A WPF or background error still ends the tray, as before,
    /// but says why first.
    /// </summary>
    private void LogUnhandledErrors()
    {
        System.Windows.Forms.Application.ThreadException += (_, args) =>
            TrayLog.Warn("App", "Unhandled error in a tray icon or menu action (the tray keeps running)", args.Exception);
        DispatcherUnhandledException += (_, args) =>
            TrayLog.Warn("App", "Unhandled error on the UI thread; the tray closes", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            TrayLog.Warn("App", "Unhandled error; the tray closes", args.ExceptionObject as Exception);
    }

    private static string? ArgAfter(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The tray icon goes first: Windows delivers clicks on a visible icon even while the services
        // below stop, and such a click reached a Homebase window already closed by the shutdown.
        _trayIcon?.Dispose();
        Services.Mobile.MobileHost.Stop();
        _forwardingHost?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
