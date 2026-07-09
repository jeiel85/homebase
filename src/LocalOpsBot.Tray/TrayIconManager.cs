using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LocalOpsBot.Core.Updates;
using LocalOpsBot.Tray.Services;
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
    private readonly UpdateService _updater;
    private readonly ToolStripMenuItem _updateItem;
    private SettingsWindow? _popup;
    private OnboardingWindow? _onboarding;

    public TrayIconManager()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Homebase.Tray/0.1");
        _updater = new UpdateService(http);

        _trayIcon = new NotifyIcon
        {
            Text = "Homebase",
            Icon = LoadTrayIcon(),
            Visible = true
        };
        _trayIcon.MouseClick += OnTrayClick;

        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open Homebase");
        openItem.Click += (_, _) => TogglePopup();
        menu.Items.Add(openItem);

        var setupItem = new ToolStripMenuItem("Setup / Welcome");
        setupItem.Click += (_, _) => ShowOnboarding();
        menu.Items.Add(setupItem);

        _updateItem = new ToolStripMenuItem($"v{_updater.GetCurrentVersionString()} — Check for Updates");
        _updateItem.Click += async (_, _) => await CheckForUpdatesAsync();
        menu.Items.Add(_updateItem);

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;

        _ = BackgroundCheckAsync();

        // First launch for this Windows user: walk them through the connection checklist
        // once. Guarded so a window failure can never take down the tray itself; it only
        // ever shows again on demand from the "Setup / Welcome" menu item.
        try
        {
            if (!OnboardingState.IsCompleted())
                ShowOnboarding();
        }
        catch { /* onboarding is non-essential — never let it break tray startup */ }
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) TogglePopup();
    }

    private void TogglePopup()
    {
        _popup ??= new SettingsWindow();
        // Toggle only when it is already the foreground window; if it is hidden OR just behind
        // another window, bring it to the front instead of hiding it, so a tray click always
        // surfaces the dashboard rather than dismissing an out-of-sight one.
        if (_popup is { IsVisible: true, IsActive: true }) _popup.Hide();
        else _popup.ShowNearTray();
    }

    private void ShowPopup()
    {
        _popup ??= new SettingsWindow();
        if (_popup.IsVisible) _popup.Activate();
        else _popup.ShowNearTray();
    }

    private void ShowOnboarding()
    {
        if (_onboarding is { IsVisible: true })
        {
            _onboarding.Activate();
            return;
        }
        _onboarding = new OnboardingWindow();
        // Open the dashboard only after onboarding has fully closed — deferring past the
        // close avoids a focus race with SettingsWindow's hide-on-deactivate behaviour.
        _onboarding.GetStartedRequested += () =>
            Application.Current.Dispatcher.BeginInvoke(new Action(ShowPopup));
        _onboarding.Closed += (_, _) => _onboarding = null;
        _onboarding.Show();
        _onboarding.Activate();
    }

    private async Task BackgroundCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            var info = await _updater.CheckForUpdateAsync(CancellationToken.None);
            if (info != null)
                _updateItem.Text = $"📡 Update v{info.Version} available!";
        }
        catch { /* update check is best-effort */ }
    }

    private async Task CheckForUpdatesAsync()
    {
        _updateItem.Text = "Checking…";
        _updateItem.Enabled = false;
        try
        {
            var info = await _updater.CheckForUpdateAsync(CancellationToken.None);
            if (info == null)
            {
                _updateItem.Text = $"✅ v{_updater.GetCurrentVersionString()} — up to date";
                MessageDialog.Show("Update Check",
                    $"Homebase is up to date (v{_updater.GetCurrentVersionString()}).");
            }
            else
            {
                var confirmed = MessageDialog.Show("Update Available",
                    $"Update v{info.Version} available!\nPublished: {info.PublishedAt:yyyy-MM-dd}",
                    primary: "Download & Install", secondary: "Later");
                if (confirmed)
                {
                    _updateItem.Text = "Downloading update…";
                    var zip = await _updater.DownloadUpdateAsync(info, null, CancellationToken.None);
                    _updater.ApplyUpdate(zip);
                    Application.Current.Shutdown();
                }
                else
                {
                    _updateItem.Text = $"📡 v{info.Version} available";
                }
            }
        }
        catch (Exception ex)
        {
            _updateItem.Text = "⚠️ Update check failed";
            MessageDialog.Show("Update Error", $"Update check failed: {ex.Message}");
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
            var uri = new Uri("pack://application:,,,/Resources/homebase.ico");
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

    public void Dispose()
    {
        _trayIcon.Visible = false;
        _trayIcon.Icon?.Dispose();
        _trayIcon.Dispose();
    }
}
