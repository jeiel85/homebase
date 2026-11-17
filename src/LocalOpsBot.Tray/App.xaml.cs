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
        // Design preview of the Homebase window with made-up devices and notifications (no tray icon, no
        // mutex). Settings is the real page: it reads this PC's configuration and, on the Phone area,
        // asks this PC's Agent for a pairing code as the tray does:
        //   Homebase.Tray.exe --preview-dashboard [--view devices|notifications|settings]
        //                     [--area phone|telegram|forwarding|monitors|updates] [--select <device key>] [--lang en]
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
            return;
        }

        // Hold the mutex for the process lifetime; createdNew == false means another tray in this
        // session already owns it, so bail out before creating a second icon / pipe client.
        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ApplyLanguage();
        _trayIcon = new TrayIconManager();

        // Start toast-notification forwarding if the user has enabled it. This is the tray half of
        // the pipeline (listener → pipe → Agent → Telegram); it stays dormant until enabled.
        _forwardingHost = new NotificationForwardingHost();
        _forwardingHost.StartIfEnabled();

        // Mobile monitoring bridge: connects to the Agent but collects nothing until a phone watches.
        Services.Mobile.MobileHost.StartIfEnabled();
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

    private static string? ArgAfter(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.Mobile.MobileHost.Stop();
        _forwardingHost?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
