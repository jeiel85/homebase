using System.Windows;

namespace LocalOpsBot.Tray;

public partial class App : Application
{
    private TrayIconManager? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _trayIcon = new TrayIconManager();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
