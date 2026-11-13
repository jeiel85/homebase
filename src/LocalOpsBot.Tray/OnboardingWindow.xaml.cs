using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Tray.Services;

namespace LocalOpsBot.Tray;

/// <summary>
/// First-run setup checklist. It probes what is ready (Agent service, Telegram connection,
/// optional Ollama) and points at the next step for each item. It never writes config itself:
/// the Telegram connection is entered in <see cref="SettingsWindow"/>, reached from the
/// "Connect Telegram" button (<see cref="OpenTelegramSettingsRequested"/>).
/// </summary>
public partial class OnboardingWindow : ThemedWindow
{
    private readonly ConnectionReadiness _readiness = new();
    private string _ollamaModel = "llama3.2:1b";

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

        // AI advice is optional and network-bound: probe it separately with a short timeout so a
        // slow or absent Ollama never blocks or fails the Telegram checklist above.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            ApplyOllama(await _readiness.ProbeOllamaAsync(cts.Token));
        }
        catch
        {
            ApplyOllama(new OllamaProbe(OllamaReadiness.Unreachable, _ollamaModel, "", null));
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

    private void ApplyOllama(OllamaProbe p)
    {
        _ollamaModel = p.Model;
        OllamaInstallHint.Visibility = Visibility.Collapsed;
        OllamaStartButton.Visibility = Visibility.Collapsed;
        OllamaPullHint.Visibility = Visibility.Collapsed;

        switch (p.Status)
        {
            case OllamaReadiness.Ready:
                SetChip(OllamaChip, OllamaChipText, Chip.Ready);
                OllamaStatusText.Text = L.OllamaReady(p.Model);
                break;
            case OllamaReadiness.ModelMissing:
                SetChip(OllamaChip, OllamaChipText, Chip.Setup);
                OllamaStatusText.Text = L.OllamaModelMissing;
                OllamaPullCommand.Text = $"ollama pull {p.Model}";
                OllamaPullHint.Visibility = Visibility.Visible;
                OllamaPullButton.IsEnabled = IsSafeModelName(p.Model);
                break;
            default: // Unreachable
                OllamaChip.Background = (Brush)FindResource("Brush.Hairline");
                OllamaChipText.Text = L.ChipOptional;
                if (OllamaSetup.IsInstalled())
                {
                    // Installed but not running — the common case. One click starts it + autostart.
                    OllamaStatusText.Text = L.OllamaNotRunning;
                    OllamaStartButton.Visibility = Visibility.Visible;
                }
                else
                {
                    OllamaStatusText.Text = L.OllamaNotInstalled;
                    OllamaInstallHint.Visibility = Visibility.Visible;
                }
                break;
        }
    }

    private void OllamaPull_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSafeModelName(_ollamaModel))
        {
            OllamaStatusText.Text = L.OllamaBadModel;
            return;
        }
        try
        {
            // Open a console running the pull so the user can watch download progress; ollama is
            // a user-level CLI, so this needs no elevation.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k ollama pull {_ollamaModel}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            OllamaStatusText.Text = L.OllamaPullFailed(ex.Message);
        }
    }

    // One click: register Ollama to run at login (the missing piece) and start it now, then re-probe.
    private async void OllamaStart_Click(object sender, RoutedEventArgs e)
    {
        OllamaStartButton.IsEnabled = false;
        OllamaStatusText.Text = L.OllamaStarting;
        try
        {
            OllamaSetup.StartAndEnableAutostart();
            await Task.Delay(4000); // give the server a moment to come up before re-probing
            Refresh();
        }
        finally
        {
            OllamaStartButton.IsEnabled = true;
        }
    }

    // Only shell out for model names built from the characters Ollama actually uses, so a stray
    // llmAdvisor.model config value can't smuggle shell metacharacters into the pull command.
    private static bool IsSafeModelName(string model) =>
        !string.IsNullOrWhiteSpace(model)
        && model.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/');

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

    // Onboarding is a once-shown, opt-in guide: mark it done on any close (X or Get started)
    // so it never auto-nags again, while the tray menu keeps it reopenable.
    protected override void OnClosed(EventArgs e)
    {
        OnboardingState.MarkCompleted();
        base.OnClosed(e);
    }
}
