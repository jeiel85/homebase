using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Tray.Services;

namespace LocalOpsBot.Tray;

/// <summary>
/// First-run welcome window. It probes whether the machine is ready to talk to Telegram
/// (Agent service, bot token, chat allowlist) and lets the user fire a test message — it
/// never writes config, so it needs no elevation. Credential changes stay with the
/// installer; onboarding only guides and verifies.
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly ConnectionReadiness _readiness = new();
    private string _ollamaModel = "llama3.2:1b";

    /// <summary>Raised when the user clicks "Get started"; the tray opens the dashboard.</summary>
    public event Action? GetStartedRequested;

    public OnboardingWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private enum Chip { Ready, Setup, Missing }

    private async void Refresh()
    {
        RecheckButton.IsEnabled = false;
        try
        {
            var snapshot = await Task.Run(_readiness.Probe);
            Apply(snapshot);
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
                ServiceStatusText.Text = "Running — monitoring is active.";
                break;
            case AgentServiceState.Stopped:
                SetChip(ServiceChip, ServiceChipText, Chip.Setup);
                ServiceStatusText.Text = "Installed but stopped. Start the LocalOpsBot.Agent service.";
                break;
            default:
                SetChip(ServiceChip, ServiceChipText, Chip.Missing);
                ServiceStatusText.Text = "Not installed. Run the installer to set up the service.";
                break;
        }

        if (s.TokenConfigured)
        {
            SetChip(TokenChip, TokenChipText, Chip.Ready);
            TokenStatusText.Text = "Bot token is set.";
        }
        else
        {
            SetChip(TokenChip, TokenChipText, Chip.Setup);
            TokenStatusText.Text = "No bot token yet. Create one with @BotFather, then re-run the installer.";
        }

        if (s.ChatConfigured)
        {
            SetChip(ChatChip, ChatChipText, Chip.Ready);
            ChatStatusText.Text = $"Chat ID {s.PrimaryChatId} is allowed.";
        }
        else
        {
            SetChip(ChatChip, ChatChipText, Chip.Setup);
            ChatStatusText.Text = "No chat ID yet. Message your bot, then re-run the installer.";
        }

        TestButton.IsEnabled = s is { TokenConfigured: true, ChatConfigured: true };
    }

    private void ApplyOllama(OllamaProbe p)
    {
        _ollamaModel = p.Model;
        OllamaInstallHint.Visibility = Visibility.Collapsed;
        OllamaPullHint.Visibility = Visibility.Collapsed;

        switch (p.Status)
        {
            case OllamaReadiness.Ready:
                SetChip(OllamaChip, OllamaChipText, Chip.Ready);
                OllamaStatusText.Text = $"Ready — model '{p.Model}' is installed.";
                break;
            case OllamaReadiness.ModelMissing:
                SetChip(OllamaChip, OllamaChipText, Chip.Setup);
                OllamaStatusText.Text = "Server is running, but the model isn't pulled yet.";
                OllamaPullCommand.Text = $"ollama pull {p.Model}";
                OllamaPullHint.Visibility = Visibility.Visible;
                break;
            default: // Unreachable
                SetChip(OllamaChip, OllamaChipText, Chip.Setup);
                OllamaStatusText.Text = "Not detected. Optional — install Ollama to enable AI advice.";
                OllamaInstallHint.Visibility = Visibility.Visible;
                break;
        }
    }

    private void OllamaPull_Click(object sender, RoutedEventArgs e)
    {
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
            OllamaStatusText.Text = $"Couldn't start the pull: {ex.Message}";
        }
    }

    private void SetChip(Border chip, TextBlock text, Chip level)
    {
        var (key, label) = level switch
        {
            Chip.Ready => ("Brush.StatusOnline", "READY"),
            Chip.Setup => ("Brush.StatusWarn", "SETUP"),
            _ => ("Brush.StatusBad", "MISSING"),
        };
        chip.Background = (Brush)FindResource(key);
        text.Text = label;
    }

    private async void TestMessage_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        RecheckButton.IsEnabled = false;
        TestResultText.Foreground = (Brush)FindResource("Brush.InkSoft");
        TestResultText.Text = "Sending…";
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
            TestResultText.Text = $"Send failed: {ex.Message}";
        }
        finally
        {
            RecheckButton.IsEnabled = true;
            Refresh(); // re-probe and re-enable the test button if still configured
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
            // No browser / handler available — silently ignore.
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
