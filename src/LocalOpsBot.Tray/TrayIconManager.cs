using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using LocalOpsBot.Core.Updates;

namespace LocalOpsBot.Tray;

public sealed class TrayIconManager : IDisposable
{
    private readonly TaskbarIcon _trayIcon;
    private readonly MenuItem _updateItem;
    private readonly UpdateService _updater;

    public TrayIconManager()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalOpsBot.Tray/0.1");
        _updater = new UpdateService(http);

        _trayIcon = new TaskbarIcon();
        _trayIcon.ToolTipText = "LocalOps Bot";

        var contextMenu = new ContextMenu();
        var statusItem = new MenuItem { Header = "Status: Agent connected", IsEnabled = false };
        var forwardingItem = new MenuItem { Header = "Notification Forwarding: Off", IsEnabled = false };
        var separator1 = new MenuItem();
        var settingsItem = new MenuItem { Header = "Open Settings" };
        settingsItem.Click += (_, _) => OpenSettings();
        var separator2 = new MenuItem();
        _updateItem = new MenuItem { Header = $"v{_updater.GetCurrentVersionString()} \u2014 Check for Updates" };
        _updateItem.Click += async (_, _) => await CheckForUpdatesAsync();
        var separator3 = new MenuItem();
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        contextMenu.Items.Add(statusItem);
        contextMenu.Items.Add(forwardingItem);
        contextMenu.Items.Add(separator1);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(separator2);
        contextMenu.Items.Add(_updateItem);
        contextMenu.Items.Add(separator3);
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenu = contextMenu;

        _ = BackgroundCheckAsync();
    }

    private async Task BackgroundCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            var info = await _updater.CheckForUpdateAsync(CancellationToken.None);
            if (info != null)
            {
                _updateItem.Header = $"\ud83d\udce1 Update v{info.Version} available!";
                _updateItem.FontWeight = FontWeights.Bold;
            }
        }
        catch { }
    }

    private async Task CheckForUpdatesAsync()
    {
        _updateItem.Header = "Checking...";
        _updateItem.IsEnabled = false;

        try
        {
            var info = await _updater.CheckForUpdateAsync(CancellationToken.None);
            if (info == null)
            {
                _updateItem.Header = $"\u2705 v{_updater.GetCurrentVersionString()} \u2014 up to date";
                MessageBox.Show($"LocalOps Bot is up to date (v{_updater.GetCurrentVersionString()}).", "Update Check", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var result = MessageBox.Show($"Update v{info.Version} available!\nPublished: {info.PublishedAt:yyyy-MM-dd}\n\nDownload and install now?", "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _updateItem.Header = "Downloading update...";
                    var zip = await _updater.DownloadUpdateAsync(info, null, CancellationToken.None);
                    _updater.ApplyUpdate(zip);
                    Application.Current.Shutdown();
                }
                else
                {
                    _updateItem.Header = $"\ud83d\udce1 v{info.Version} available";
                }
            }
        }
        catch (Exception ex)
        {
            _updateItem.Header = $"\u26a0\ufe0f Update check failed";
            MessageBox.Show($"Update check failed: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateItem.IsEnabled = true;
        }
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
