using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace LocalOpsBot.Tray;

public sealed class TrayIconManager : IDisposable
{
    private readonly TaskbarIcon _trayIcon;

    public TrayIconManager()
    {
        _trayIcon = new TaskbarIcon();
        _trayIcon.ToolTipText = "LocalOps Bot";

        var contextMenu = new ContextMenu();
        var statusItem = new MenuItem { Header = "Status: Agent connected", IsEnabled = false };
        var forwardingItem = new MenuItem { Header = "Notification Forwarding: Off", IsEnabled = false };
        var separator1 = new MenuItem();
        var settingsItem = new MenuItem { Header = "Open Settings" };
        settingsItem.Click += (_, _) => OpenSettings();
        var separator2 = new MenuItem();
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        contextMenu.Items.Add(statusItem);
        contextMenu.Items.Add(forwardingItem);
        contextMenu.Items.Add(separator1);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(separator2);
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenu = contextMenu;
    }

    private static void OpenSettings()
    {
        var window = new SettingsWindow();
        window.ShowDialog();
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
    }
}
