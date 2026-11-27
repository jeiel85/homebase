using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Text.Json;
using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Tray.Services;

namespace LocalOpsBot.Tray;

public enum SettingsTab { Phone, Telegram, Forwarding, Monitors, Ai, Updates }

/// <summary>One entry of the AI advice model picker: the model name and a note after it (empty, or
/// whether it is the model in use).</summary>
public sealed record ModelPick(string Name, string Note);

/// <summary>
/// Everything that changes how Homebase behaves, one area per item down the left — the dashboard's
/// Settings tab (DECISIONS M-49). Each area states what it does, shows the current state, and says
/// before the click when a change needs administrator approval and what restarts — nothing elevates
/// on a checkbox click any more.
/// </summary>
public partial class SettingsPanel : UserControl
{
    private readonly ConnectionReadiness _readiness = new();
    private UpdateCoordinator? _updates;
    private bool _forwardingPersisted;
    private string _monitorsLoaded = "";
    private bool _loadingMonitors;
    private bool _inView;
    private bool _loaded;
    private string? _aiCurrent; // the picker entry that is the model in use; null before the first probe
    private bool _savingModel;
    private bool _aiReady;
    private bool _askingAdvice;

    public SettingsPanel()
    {
        InitializeComponent();
        OllamaPanel.Probed += OnOllamaProbed;
        var ver = typeof(SettingsPanel).Assembly.GetName().Version;
        AboutVersionText.Text = $"Homebase v{ver?.ToString(3) ?? "?"}";
    }

    /// <summary>The tray's update flow, shared with its menu item (set once by the dashboard).</summary>
    internal void Attach(UpdateCoordinator updates)
    {
        _updates = updates;
        UpdateVersionText.Text = L.UpdCurrent(updates.CurrentVersion);
    }

    public void ShowTab(SettingsTab tab) => Tabs.SelectedItem = tab switch
    {
        SettingsTab.Telegram => TelegramTab,
        SettingsTab.Forwarding => ForwardingTab,
        SettingsTab.Monitors => MonitorsTab,
        SettingsTab.Ai => AiTab,
        SettingsTab.Updates => UpdatesTab,
        _ => PhoneTab,
    };

    /// <summary>
    /// The dashboard calls this when the tab comes into view (picked, or the window shown or restored)
    /// and when it leaves (another tab, hidden, minimized). Nothing is read until it is first shown;
    /// after that, switching tabs keeps what was typed — only the Telegram status is looked at again.
    /// </summary>
    internal void SetInView(bool inView)
    {
        if (inView == _inView) return;
        _inView = inView;
        if (inView)
        {
            RefreshTelegram();
            if (!_loaded)
            {
                _loaded = true;
                LoadForwardingState();
                LoadMonitors();
                LoadAiAutomation();
            }
        }
        SyncPhonePanel();
        RefreshAiIfShown();
    }

    /// <summary>
    /// The window was hidden — what closing the Settings window used to be: forget unsaved input
    /// (the bot token above all, the chat ID, the Phone area's edits) and a shown pairing code, and
    /// read everything afresh next time (the chat ID is filled in again from the saved one).
    /// </summary>
    internal void Discard()
    {
        TokenInput.Clear();
        ChatIdInput.Clear();
        // An unsaved pick is forgotten; the next probe fills the picker in again.
        _aiCurrent = null;
        _aiReady = false;
        ModelPicker.ItemsSource = null;
        ModelPicker.IsEnabled = false;
        AiModelNoneText.Visibility = Visibility.Collapsed;
        PhonePanel.Forget();
        TgResultText.Text = AppsStatusText.Text = MonitorsStatusText.Text = AiModelStatusText.Text = AdviceStatusText.Text = AiAutoStatusText.Text = string.Empty;
        AdviceBox.Visibility = Visibility.Collapsed;
        _loaded = false;
    }

    /// <summary>The tray is shutting down.</summary>
    internal void Stop() => PhonePanel.Stop();

    // The phone area talks to the Agent (status poll, pairing code) only while it is in view.
    // SelectionChanged bubbles up from lists inside the tabs too, so only the TabControl's own counts.
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        SyncPhonePanel();
        RefreshAiIfShown();
    }

    private void SyncPhonePanel()
    {
        if (_inView && Tabs.SelectedItem == PhoneTab) PhonePanel.Show();
        else PhonePanel.Hide();
    }

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
        FwAutostartWarning.Visibility = _forwardingPersisted && TrayAutostart.IsDisabled() ? Visibility.Visible : Visibility.Collapsed;
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
                MessageDialog.Show(L.FwConfirmTitle(enable), L.FwNotSaved, L.Ok, owner: Window.GetWindow(this));
                LoadForwardingState();
                return;
            }

            _forwardingPersisted = enable;
            LoadForwardingState();
            // The tray must restart to start/stop the notification listener with the new state.
            if (MessageDialog.Show(L.FwConfirmTitle(enable), L.FwRestart(enable), L.RestartNow, L.Later, owner: Window.GetWindow(this)))
                RestartTray();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(L.FwConfirmTitle(enable), L.Failed(ex.Message), L.Ok, owner: Window.GetWindow(this));
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

    // ── AI advice (Ollama) ─────────────────────────────────────────────────────────────────────
    // Ollama is probed each time the area is shown (it is started and stopped outside Homebase), and
    // on "re-check" after a pull finishes in its console window.
    private void RefreshAiIfShown()
    {
        if (_inView && Tabs.SelectedItem == AiTab) _ = RefreshAiAsync();
    }

    private async void AiRecheck_Click(object sender, RoutedEventArgs e) => await RefreshAiAsync();

    private async Task RefreshAiAsync(bool afterChange = false)
    {
        AiRecheckButton.IsEnabled = false;
        try { await (afterChange ? OllamaPanel.RefreshAfterChangeAsync() : OllamaPanel.RefreshAsync()); }
        finally { AiRecheckButton.IsEnabled = true; }
    }

    // "Get advice now" (M-60): the Agent's shared on-demand advice, the same the phone app asks for.
    private async void Advise_Click(object sender, RoutedEventArgs e)
    {
        if (Services.Mobile.MobileHost.Client is not { IsConnected: true } bridge)
        {
            SetResult(AdviceStatusText, L.AiAskNoAgent, false);
            return;
        }
        _askingAdvice = true;
        UpdateAdviceAvailability();
        SetResult(AdviceStatusText, L.AiAskWorking, null);
        try
        {
            // The model may have to load first; the Agent gives up after llmAdvisor.timeoutSeconds (60 s).
            var r = await bridge.RequestAsync(MobileBridge.OpAdvise, timeout: TimeSpan.FromSeconds(90));
            if ((bool?)r["ok"] != true)
            {
                SetResult(AdviceStatusText, MobileBridge.StringOf(r["error"]) == MobileBridge.ErrorUnsupported
                    ? L.AiAskOldAgent : L.Failed(MobileBridge.StringOf(r["error"]) ?? "?"), false);
                return;
            }
            var advice = r["payload"]?.Deserialize<AdviceResponse>(MobileProtocol.Json);
            if (advice is null) { SetResult(AdviceStatusText, L.Failed("?"), false); return; }
            ShowAdvice(advice);
        }
        catch (OperationCanceledException)
        {
            // Our own 90 s limit: the Agent is there, the model is just slow.
            SetResult(AdviceStatusText, L.AiAskError(AdviceErrors.Timeout, null), false);
        }
        catch (JsonException ex)
        {
            SetResult(AdviceStatusText, L.Failed(ex.Message), false);
        }
        catch (System.IO.IOException)
        {
            SetResult(AdviceStatusText, L.AiAskNoAgent, false);
        }
        finally
        {
            _askingAdvice = false;
            UpdateAdviceAvailability();
        }
    }

    internal void ShowAdvice(AdviceResponse advice)
    {
        if (!advice.Ok)
        {
            AdviceBox.Visibility = Visibility.Collapsed;
            SetResult(AdviceStatusText, L.AiAskError(advice.Error, advice.Message), false);
            // Not running or the model missing: the row above now offers Start / Run pull.
            if (advice.Error is AdviceErrors.Unreachable or AdviceErrors.ModelMissing) _ = RefreshAiAsync();
            return;
        }
        AdviceStatusText.Text = string.Empty;
        AdviceText.Text = advice.Text ?? "";
        AdviceMeta.Text = L.AiAskMeta(advice.Model ?? "?", advice.GeneratedAt.ToLocalTime(), advice.Cached);
        AdviceBasis.Text = advice.Summary ?? "";
        AdviceBox.Visibility = Visibility.Visible;
    }

    // Each probe re-reads the config and what the server has pulled: the picker lists the pulled models
    // with the configured one marked and picked — unless another one was picked and not saved yet, which
    // stays picked while the server still has it.
    private void OnOllamaProbed(OllamaProbe p)
    {
        var pending = PickedModel() is { } picked && !SameModel(picked, _aiCurrent) ? picked : null;
        var choices = LlmAdvisorConfigurator.ChoicesFor(p.Model, p.Installed);
        var picks = choices.Models
            .Select(m => new ModelPick(m, SameModel(m, choices.Current) ? "  " + L.AiModelInUse : ""))
            .ToList();

        _aiCurrent = choices.Current;
        _aiReady = p.Status == OllamaReadiness.Ready && choices.CurrentPulled;
        ModelPicker.ItemsSource = picks;
        ModelPicker.SelectedItem = picks.FirstOrDefault(m => SameModel(m.Name, pending))
            ?? picks.FirstOrDefault(m => SameModel(m.Name, choices.Current))
            ?? picks.FirstOrDefault();
        ModelPicker.IsEnabled = p.Installed.Count > 0;
        AiModelNoneText.Visibility = p.Installed.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateModelSave();
    }

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateModelSave();

    private string? PickedModel() => (ModelPicker.SelectedItem as ModelPick)?.Name;

    // Ollama matches model names without regard to case.
    private static bool SameModel(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // SAVE is offered only for a pick that isn't the model already in use, and not while a save runs.
    private void UpdateModelSave()
    {
        SaveModelButton.IsEnabled = !_savingModel && PickedModel() is { } m && !SameModel(m, _aiCurrent);
        UpdateAdviceAvailability();
    }

    private void UpdateAdviceAvailability() =>
        AdviseButton.IsEnabled = _aiReady && !_askingAdvice && !_savingModel
            && PickedModel() is { } picked && SameModel(picked, _aiCurrent);

    private async void SaveModel_Click(object sender, RoutedEventArgs e)
    {
        if (PickedModel() is not { } model) return;
        // Checked before raising UAC so a typo or a no-op never costs an approval prompt.
        switch (LlmAdvisorConfigurator.Check(model, OllamaPanel.Model))
        {
            case ModelChangeCheck.Invalid: SetResult(AiModelStatusText, L.AiModelInvalid, false); return;
            case ModelChangeCheck.Unchanged: SetResult(AiModelStatusText, L.AiModelSame, null); return;
        }

        _savingModel = true;
        UpdateModelSave();
        SetResult(AiModelStatusText, L.AiModelSaving, null);
        try
        {
            if (!await LlmAdvisorConfigurator.SaveModelAsync(model))
            {
                SetResult(AiModelStatusText, L.AiModelNotSaved, false);
                return;
            }
            // The probe reads the config just written: it shows the new model, and RUN PULL if it
            // isn't on this PC yet.
            await RefreshAiAsync(afterChange: true);
            if (LlmAdvisorConfigurator.ServiceOverride() is { } overriding
                && !string.Equals(overriding, model, StringComparison.OrdinalIgnoreCase))
                SetResult(AiModelStatusText, L.AiModelOverridden(overriding), false);
            else
                SetResult(AiModelStatusText, L.AiModelSaved(model), true);
        }
        catch (Exception ex)
        {
            SetResult(AiModelStatusText, L.Failed(ex.Message), false);
        }
        finally
        {
            _savingModel = false;
            UpdateModelSave();
        }
    }

    // ── The AI on its own (M-61): auto advice, event-log interpretation ─────────────────────────────
    // The checkboxes only stage the change; APPLY writes both with one approval (the Agent restarts).
    private AiAutomationState? _aiAuto;

    private void LoadAiAutomation()
    {
        try { _aiAuto = AiAutomationConfig.Read(TrayConfig.Load()); }
        catch { _aiAuto = null; }
        AutoAdviceCheckBox.IsChecked = _aiAuto?.AutoAdvice == true;
        InterpretCheckBox.IsChecked = _aiAuto?.InterpretErrors == true;
        AutoAdviceCheckBox.IsEnabled = InterpretCheckBox.IsEnabled = _aiAuto is not null;
        AutoAdviceHint.Text = _aiAuto is { } s
            ? L.AiAutoAdviceHint(s.CpuPercent, s.MemoryPercent, s.DiskPercent, s.TempCelsius, s.SustainMinutes, s.CooldownMinutes)
            : "";
        InterpretHint.Text = _aiAuto is { EventLogOn: false } ? L.AiInterpretHintOff : L.AiInterpretHint;
        ApplyAiAutoButton.IsEnabled = false;
    }

    private bool AiAutomationDirty() => _aiAuto is { } s
        && ((AutoAdviceCheckBox.IsChecked == true) != s.AutoAdvice || (InterpretCheckBox.IsChecked == true) != s.InterpretErrors);

    private void AiAutomation_Click(object sender, RoutedEventArgs e) => ApplyAiAutoButton.IsEnabled = AiAutomationDirty();

    private async void ApplyAiAutomation_Click(object sender, RoutedEventArgs e)
    {
        var autoAdvice = AutoAdviceCheckBox.IsChecked == true;
        var interpret = InterpretCheckBox.IsChecked == true;
        ApplyAiAutoButton.IsEnabled = false;
        SetResult(AiAutoStatusText, L.AiModelSaving, null);
        try
        {
            if (await AiAutomationConfig.SaveAsync(autoAdvice, interpret))
            {
                LoadAiAutomation(); // reads back what was written
                SetResult(AiAutoStatusText, L.AiAutoSaved, true);
                return;
            }
            SetResult(AiAutoStatusText, L.AiModelNotSaved, false);
        }
        catch (Exception ex)
        {
            SetResult(AiAutoStatusText, L.Failed(ex.Message), false);
        }
        ApplyAiAutoButton.IsEnabled = AiAutomationDirty();
    }

    // ── Updates ────────────────────────────────────────────────────────────────────────────────
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is null) return;
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
