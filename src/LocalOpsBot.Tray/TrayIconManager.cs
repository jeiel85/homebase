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
    private DashboardWindow? _popup;
    private SettingsWindow? _settings;
    private OnboardingWindow? _onboarding;

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

        // Look (dashboard) → change (settings, phone included), then housekeeping. Labels follow the UI
        // language (see L); "Open Homebase" is bold as the left-click default.
        var openItem = new ToolStripMenuItem(L.MenuOpen) { Font = new Font(menu.Font, FontStyle.Bold) };
        openItem.Click += (_, _) => TogglePopup();
        menu.Items.Add(openItem);

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
        exitItem.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;

        // A phone watching this PC turns on per-process sampling and window reports (DECISIONS M-13),
        // so say so where the user always looks: a dot on the tray icon, a tooltip and a menu line
        // (M-22). The bridge raises this on a background thread.
        MobileHost.WatchingChanged += OnWatchingChanged;
        ApplyWatching(MobileHost.IsWatching);
        MobileHost.ToastRequested += OnToastRequested;

        _ = BackgroundCheckAsync();

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
            // NotifyIcon limits: title 63, text 255 characters; an empty text shows no balloon at all.
            var title = Clip(toast.Title, 63);
            var text = Clip(string.IsNullOrWhiteSpace(toast.Body) ? " " : toast.Body!, 255);
            OwnToasts.Remember(title);
            try { _trayIcon.ShowBalloonTip(6000, title, text, ToolTipIcon.None); }
            catch (Exception) { /* the shell refused (focus assist etc.): the phones still get it */ }
        }));

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

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

    private DashboardWindow Dashboard()
    {
        if (_popup is null)
        {
            _popup = new DashboardWindow();
            _popup.OpenSettingsRequested += () => ShowSettings(SettingsTab.Phone);
        }
        return _popup;
    }

    private void TogglePopup()
    {
        var popup = Dashboard();
        // Toggle only when it is already the foreground window; if it is hidden OR just behind
        // another window, bring it to the front instead of hiding it, so a tray click always
        // surfaces the dashboard rather than dismissing an out-of-sight one.
        if (popup is { IsVisible: true, IsActive: true }) popup.Hide();
        else popup.ShowNearTray();
    }

    private void ShowPopup()
    {
        var popup = Dashboard();
        if (popup.IsVisible) popup.Activate();
        else popup.ShowNearTray();
    }

    private void ShowSettings(SettingsTab tab)
    {
        if (_settings is not { IsVisible: true })
        {
            _settings = new SettingsWindow(_updates);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        _settings.ShowTab(tab);
        if (_settings.WindowState == System.Windows.WindowState.Minimized)
            _settings.WindowState = System.Windows.WindowState.Normal;
        _settings.Activate();
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
        // close avoids a focus race with the dashboard's show-near-tray activation.
        _onboarding.GetStartedRequested += () =>
            Application.Current.Dispatcher.BeginInvoke(new Action(ShowPopup));
        _onboarding.OpenTelegramSettingsRequested += () => ShowSettings(SettingsTab.Telegram);
        _onboarding.Closed += (_, _) => _onboarding = null;
        _onboarding.Show();
        _onboarding.Activate();
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
            catch
            {
                // Rate limit / API error / retries exhausted: background check is best-effort and the
                // menu item still checks on demand.
                return;
            }

            if (info is null) return;
            _updateItem.Text = L.MenuUpdateAvailable(info.Version);

            // Don't stack the update dialog on top of first-run onboarding; the menu label is enough.
            if (_onboarding is not null) return;

            var status = new Progress<string>(text => _updateItem.Text = text);
            try
            {
                await _updates.OfferStartupUpdateAsync(info, status);
            }
            catch (Exception ex)
            {
                MessageDialog.Show(L.UpdErrorTitle, L.UpdShowFailed(ex.Message));
            }
            return;
        }
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
        MobileHost.WatchingChanged -= OnWatchingChanged;
        MobileHost.ToastRequested -= OnToastRequested;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _baseIcon.Dispose();
        _watchingIcon?.Dispose();
    }
}
