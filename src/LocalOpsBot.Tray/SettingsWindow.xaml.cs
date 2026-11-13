using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using LocalOpsBot.Tray.Services;

namespace LocalOpsBot.Tray;

public enum SettingsTab { Phone, Telegram, Forwarding, Monitors, Updates }

/// <summary>
/// Everything that changes how Homebase behaves, one tab per area. Each tab states what it does,
/// shows the current state, and says before the click when a change needs administrator approval
/// and what restarts — nothing elevates on a checkbox click any more.
/// </summary>
public partial class SettingsWindow : ThemedWindow
{
    private readonly UpdateCoordinator _updates;
    private readonly ConnectionReadiness _readiness = new();
    private bool _forwardingPersisted;
    private string _monitorsLoaded = "";
    private bool _loadingMonitors;

    internal SettingsWindow(UpdateCoordinator updates)
    {
        InitializeComponent();
        _updates = updates;

        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        AboutVersionText.Text = $"Homebase v{ver?.ToString(3) ?? "?"}";
        UpdateVersionText.Text = L.UpdCurrent(_updates.CurrentVersion);

        Loaded += (_, _) => { RefreshTelegram(); LoadForwardingState(); LoadMonitors(); };
        Closed += (_, _) => PhonePanel.Stop();
    }

    public void ShowTab(SettingsTab tab) => Tabs.SelectedItem = tab switch
    {
        SettingsTab.Telegram => TelegramTab,
        SettingsTab.Forwarding => ForwardingTab,
        SettingsTab.Monitors => MonitorsTab,
        SettingsTab.Updates => UpdatesTab,
        _ => PhoneTab,
    };

    // The phone tab talks to the Agent (status poll, pairing code) only while it is in view.
    // SelectionChanged bubbles up from lists inside the tabs too, so only the TabControl's own counts.
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedItem == PhoneTab) PhonePanel.Show();
        else PhonePanel.Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetChip(Border chip, TextBlock text, string brushKey, string label)
    {
        chip.Background = (Brush)FindResource(brushKey);
        text.Text = label;
    }

    private void SetResult(TextBlock target, string message, bool? ok)
    {
        target.Foreground = (Brush)FindResource(ok switch
        {
            true => "Brush.StatusOnline",
            false => "Brush.StatusBad",
            null => "Brush.InkSoft",
        });
        target.Text = message;
    }

    // ── Telegram ───────────────────────────────────────────────────────────────────────────────
    private async void RefreshTelegram()
    {
        try
        {
            var s = await Task.Run(_readiness.Probe);
            var service = s.Service == AgentServiceState.Running ? "" : L.TgServiceNotRunning;
            if (s is { TokenConfigured: true, ChatConfigured: true, PrimaryChatId: long chat })
            {
                SetChip(TgChip, TgChipText, s.Service == AgentServiceState.Running ? "Brush.StatusOnline" : "Brush.StatusWarn", L.ChipReady);
                TgStatusText.Text = L.TgStatusReady(chat) + service;
            }
            else
            {
                SetChip(TgChip, TgChipText, "Brush.StatusWarn", L.ChipSetup);
                TgStatusText.Text = (s.TokenConfigured ? L.TgStatusNoChat : L.TgStatusNoToken) + service;
            }
            if (string.IsNullOrEmpty(ChatIdInput.Text) && s.PrimaryChatId is long id)
                ChatIdInput.Text = id.ToString();
            TestButton.IsEnabled = s is { TokenConfigured: true, ChatConfigured: true };
        }
        catch
        {
            // Transient probe failure — keep whatever was last shown.
        }
    }

    private async void TestMessage_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        SetResult(TgResultText, L.Sending, null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await _readiness.SendTestMessageAsync(cts.Token);
            SetResult(TgResultText, result.Message, result.Ok);
        }
        catch (Exception ex)
        {
            SetResult(TgResultText, L.TestFailed(ex.Message), false);
        }
        finally
        {
            RefreshTelegram();
        }
    }

    private async void SaveTelegram_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenInput.Password.Trim();
        var chatId = ChatIdInput.Text.Trim();

        // Validate before raising UAC so a typo never costs an approval prompt.
        if (!TelegramSetup.IsValidToken(token)) { SetResult(TgResultText, L.TgInvalidToken, false); return; }
        if (!TelegramSetup.IsValidChatId(chatId)) { SetResult(TgResultText, L.TgInvalidChat, false); return; }

        SaveTelegramButton.IsEnabled = false;
        SetResult(TgResultText, L.TgApplying, null);
        try
        {
            var r = await TelegramSetup.ApplyAsync(token, chatId);
            switch (r.Outcome)
            {
                case TelegramApplyOutcome.Saved:
                    TokenInput.Clear();
                    SetResult(TgResultText, L.TgSaved, true);
                    await Task.Delay(3000); // give the service time to come back up
                    RefreshTelegram();
                    break;
                case TelegramApplyOutcome.Cancelled: SetResult(TgResultText, L.TgCancelled, false); break;
                case TelegramApplyOutcome.ScriptMissing: SetResult(TgResultText, L.TgScriptMissing, false); break;
                case TelegramApplyOutcome.HelperFailed: SetResult(TgResultText, L.TgHelperFailed, false); break;
                case TelegramApplyOutcome.InvalidToken: SetResult(TgResultText, L.TgInvalidToken, false); break;
                case TelegramApplyOutcome.InvalidChatId: SetResult(TgResultText, L.TgInvalidChat, false); break;
                default:
                    SetResult(TgResultText, r.Error is null ? L.TgApplyFailed(r.ExitCode) : L.Failed(r.Error), false);
                    break;
            }
        }
        finally
        {
            SaveTelegramButton.IsEnabled = true;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // No browser / handler available — nothing to do.
        }
        e.Handled = true;
    }

    // ── Notification forwarding ────────────────────────────────────────────────────────────────
    // The checkbox only stages the change; APPLY performs it (UAC + tray restart), so the moment
    // approval is asked for is the button the admin note points at, not a stray click.
    private void LoadForwardingState()
    {
        try { _forwardingPersisted = TrayConfig.IsNotificationForwardingEnabled(); }
        catch { _forwardingPersisted = false; }
        ForwardingEnabledCheckBox.IsChecked = _forwardingPersisted;
        SetChip(FwChip, FwChipText, _forwardingPersisted ? "Brush.StatusOk" : "Brush.Hairline", _forwardingPersisted ? L.ChipOn : L.ChipOff);
        FwCurrentText.Text = L.FwCurrent(_forwardingPersisted);
        ApplyForwardingButton.IsEnabled = false;
        UpdateAppSelectionUi();
    }

    private void ForwardingToggle_Click(object sender, RoutedEventArgs e) =>
        ApplyForwardingButton.IsEnabled = (ForwardingEnabledCheckBox.IsChecked == true) != _forwardingPersisted;

    // App selection reflects the persisted state: the list only matters while forwarding runs.
    private void UpdateAppSelectionUi()
    {
        AppSelectionPanel.Visibility = _forwardingPersisted ? Visibility.Visible : Visibility.Collapsed;
        AppsOffHint.Visibility = _forwardingPersisted ? Visibility.Collapsed : Visibility.Visible;
        if (_forwardingPersisted) PopulateAppList();
    }

    // Stored as an exclusion list (unchanged format); shown the intuitive way round — checked
    // means "forward this app".
    private void PopulateAppList()
    {
        AppListPanel.Children.Clear();
        var seen = ForwardingApps.ReadSeenApps();
        var blocked = new HashSet<string>(ForwardingApps.ReadBlockList(), StringComparer.OrdinalIgnoreCase);

        if (seen.Count == 0)
        {
            AppListPanel.Children.Add(new TextBlock
            {
                Text = L.FwNoApps,
                Style = (Style)FindResource("Type.Micro"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var app in seen.OrderBy(a => a, StringComparer.CurrentCultureIgnoreCase))
        {
            AppListPanel.Children.Add(new CheckBox
            {
                Content = app,
                IsChecked = !blocked.Contains(app),
                Margin = new Thickness(0, 2, 0, 2),
            });
        }
    }

    private void SaveApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var excluded = AppListPanel.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked != true)
                .Select(c => c.Content?.ToString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToList();
            ForwardingApps.WriteBlockList(excluded);
            SetResult(AppsStatusText, excluded.Count == 0 ? L.FwSavedAll : L.FwSavedSome(excluded.Count), true);
        }
        catch (Exception ex)
        {
            SetResult(AppsStatusText, L.Failed(ex.Message), false);
        }
    }

    private void RefreshApps_Click(object sender, RoutedEventArgs e)
    {
        PopulateAppList();
        AppsStatusText.Text = string.Empty;
    }

    private async void ApplyForwarding_Click(object sender, RoutedEventArgs e)
    {
        var enable = ForwardingEnabledCheckBox.IsChecked == true;
        ApplyForwardingButton.IsEnabled = false;
        ForwardingEnabledCheckBox.IsEnabled = false;
        try
        {
            var saved = await NotificationForwardingConfigurator.SetEnabledAsync(enable);
            if (!saved)
            {
                MessageDialog.Show(L.FwConfirmTitle(enable), L.FwNotSaved, L.Ok);
                LoadForwardingState();
                return;
            }

            _forwardingPersisted = enable;
            LoadForwardingState();
            // The tray must restart to start/stop the notification listener with the new state.
            if (MessageDialog.Show(L.FwConfirmTitle(enable), L.FwRestart(enable), L.RestartNow, L.Later))
                RestartTray();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(L.FwConfirmTitle(enable), L.Failed(ex.Message), L.Ok);
            LoadForwardingState();
        }
        finally
        {
            ForwardingEnabledCheckBox.IsEnabled = true;
        }
    }

    private static void RestartTray()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { /* best-effort — worst case the user relaunches from the Start menu */ }
        App.IsShuttingDown = true;
        System.Windows.Application.Current.Shutdown();
    }

    // ── Watch list ─────────────────────────────────────────────────────────────────────────────
    private void LoadMonitors()
    {
        _loadingMonitors = true;
        try
        {
            var m = MonitorsConfig.Read();
            ProcessesBox.Text = string.Join(Environment.NewLine, m.Processes);
            ServicesBox.Text = string.Join(Environment.NewLine, m.Services);
            HttpBox.Text = string.Join(Environment.NewLine, m.HttpEndpoints);
            TcpBox.Text = string.Join(Environment.NewLine, m.TcpPorts);
        }
        catch { /* leave blank if the config can't be read */ }
        finally
        {
            _loadingMonitors = false;
        }
        _monitorsLoaded = MonitorsFingerprint();
        UpdateMonitorsDirty();
    }

    private string MonitorsFingerprint() => string.Join("\u0001",
        new[] { ProcessesBox, ServicesBox, HttpBox, TcpBox }.Select(b => string.Join("\n", Lines(b))));

    private void Monitors_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingMonitors) UpdateMonitorsDirty();
    }

    private void UpdateMonitorsDirty()
    {
        var dirty = MonitorsFingerprint() != _monitorsLoaded;
        SaveMonitorsButton.IsEnabled = dirty;
        if (dirty) SetResult(MonitorsStatusText, L.MonDirty, null);
        else if (MonitorsStatusText.Text == L.MonDirty) MonitorsStatusText.Text = "";
    }

    private async void SaveMonitors_Click(object sender, RoutedEventArgs e)
    {
        SaveMonitorsButton.IsEnabled = false;
        SetResult(MonitorsStatusText, L.MonSaving, null);
        // Baseline is what was submitted, not what the boxes hold after the (slow, elevated) save:
        // edits made while it runs must still show as unsaved.
        var submitted = MonitorsFingerprint();
        try
        {
            var saved = await MonitorsConfigurator.SaveAsync(
                Lines(ProcessesBox), Lines(ServicesBox), Lines(HttpBox), Lines(TcpBox));
            if (saved)
            {
                _monitorsLoaded = submitted;
                if (MonitorsFingerprint() == submitted) SetResult(MonitorsStatusText, L.MonSaved, true);
                else UpdateMonitorsDirty(); // edited during the save: still unsaved
            }
            else
            {
                SetResult(MonitorsStatusText, L.MonNotSaved, false);
            }
        }
        catch (Exception ex)
        {
            SetResult(MonitorsStatusText, L.Failed(ex.Message), false);
        }
        finally
        {
            SaveMonitorsButton.IsEnabled = MonitorsFingerprint() != _monitorsLoaded;
        }
    }

    private static IEnumerable<string> Lines(TextBox box) =>
        box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    // ── Updates ────────────────────────────────────────────────────────────────────────────────
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            var status = new Progress<string>(text => UpdateStatusText.Text = text);
            await _updates.RunInteractiveAsync(status);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }
}
