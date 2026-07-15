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
        _trayIcon = new TrayIconManager();

        // Start toast-notification forwarding if the user has enabled it. This is the tray half of
        // the pipeline (listener → pipe → Agent → Telegram); it stays dormant until enabled.
        _forwardingHost = new NotificationForwardingHost();
        _forwardingHost.StartIfEnabled();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _forwardingHost?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
