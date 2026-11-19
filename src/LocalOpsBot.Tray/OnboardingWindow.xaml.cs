using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalOpsBot.Tray.Services;

namespace LocalOpsBot.Tray;

/// <summary>
/// First-run setup checklist. It probes what is ready (Agent service, Telegram connection,
/// optional Ollama) and points at the next step for each item. It never writes config itself:
/// the Telegram connection is entered on the Settings page (<see cref="SettingsPanel"/>), reached from the
/// "Connect Telegram" button (<see cref="OpenTelegramSettingsRequested"/>).
/// </summary>
public partial class OnboardingWindow : ThemedWindow
{
    private readonly ConnectionReadiness _readiness = new();

    /// <summary>Raised when the user clicks "Get started"; the tray opens the dashboard.</summary>
    public event Action? GetStartedRequested;

    /// <summary>Raised by "Connect Telegram" / "Change"; the tray opens Settings on the Telegram tab.</summary>
    public event Action? OpenTelegramSettingsRequested;

    public OnboardingWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        // Coming back from Settings: re-check so a just-saved connection shows as ready.
        Activated += (_, _) => { if (IsLoaded && RecheckButton.IsEnabled) Refresh(); };
    }

    private enum Chip { Ready, Setup, Missing }

    private async void Refresh()
    {
        RecheckButton.IsEnabled = false;
        try
        {
            Apply(await Task.Run(_readiness.Probe));
        }
        catch
        {
            // Transient probe failure — keep whatever was last shown.
        }

        // AI advice is optional and probed separately (short timeout, never throws), so a slow or
        // absent Ollama never blocks or fails the Telegram checklist above.
        try
        {
            await OllamaPanel.RefreshAsync();
        }
        finally
        {
            RecheckButton.IsEnabled = true;
        }
    }

    private void Apply(ReadinessSnapshot s)
    {
        switch (s.Service)
        {
            case AgentServiceState.Running:
                SetChip(ServiceChip, ServiceChipText, Chip.Ready);
                ServiceStatusText.Text = L.SetupServiceRunning;
                break;
            case AgentServiceState.Stopped:
                SetChip(ServiceChip, ServiceChipText, Chip.Setup);
                ServiceStatusText.Text = L.SetupServiceStopped;
                break;
            default:
                SetChip(ServiceChip, ServiceChipText, Chip.Missing);
                ServiceStatusText.Text = L.SetupServiceMissing;
                break;
        }

        var ready = s is { TokenConfigured: true, ChatConfigured: true };
        if (ready && s.PrimaryChatId is long chat)
        {
            SetChip(TelegramChip, TelegramChipText, Chip.Ready);
            TelegramStatusText.Text = L.TgStatusReady(chat);
        }
        else
        {
            SetChip(TelegramChip, TelegramChipText, Chip.Setup);
            TelegramStatusText.Text = s.TokenConfigured ? L.TgStatusNoChat : L.TgStatusNoToken;
        }

        // Ready: the next useful step is proving it works; otherwise it is entering the details.
        ConnectTelegramButton.Content = ready ? L.SetupChangeTelegram : L.SetupConnectTelegram;
        ConnectTelegramButton.Style = (Style)FindResource(ready ? "Button.Carbon" : "Button.Signal");
        TestButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        TestButton.Style = (Style)FindResource(ready ? "Button.Signal" : "Button.Carbon");
    }

    private void SetChip(Border chip, TextBlock text, Chip level)
    {
        var (key, label) = level switch
        {
            Chip.Ready => ("Brush.StatusOnline", L.ChipReady),
            Chip.Setup => ("Brush.StatusWarn", L.ChipSetup),
            _ => ("Brush.StatusBad", L.ChipMissing),
        };
        chip.Background = (Brush)FindResource(key);
        text.Text = label;
    }

    private void ConnectTelegram_Click(object sender, RoutedEventArgs e) => OpenTelegramSettingsRequested?.Invoke();

    private async void TestMessage_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestResultText.Foreground = (Brush)FindResource("Brush.InkSoft");
        TestResultText.Text = L.Sending;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await _readiness.SendTestMessageAsync(cts.Token);
            TestResultText.Foreground = (Brush)FindResource(result.Ok ? "Brush.StatusOnline" : "Brush.StatusBad");
            TestResultText.Text = result.Message;
        }
        catch (Exception ex)
        {
            TestResultText.Foreground = (Brush)FindResource("Brush.StatusBad");
            TestResultText.Text = L.TestFailed(ex.Message);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => Refresh();

    private void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        GetStartedRequested?.Invoke();
        Close();
    }

    // Onboarding is a once-shown, opt-in guide: mark it done on any close (X or Get started)
    // so it never auto-nags again, while the tray menu keeps it reopenable.
    protected override void OnClosed(EventArgs e)
    {
        OnboardingState.MarkCompleted();
        base.OnClosed(e);
    }
}
