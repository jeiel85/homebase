using System.Diagnostics;
using System.Text.RegularExpressions;
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
                ServiceStatusText.Text = "Installed but stopped. Start the Homebase.Agent service.";
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
                OllamaPullButton.IsEnabled = IsSafeModelName(p.Model);
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
        if (!IsSafeModelName(_ollamaModel))
        {
            OllamaStatusText.Text = "Model name has unexpected characters — pull it manually.";
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
            OllamaStatusText.Text = $"Couldn't start the pull: {ex.Message}";
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

    private async void SaveApply_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenInput.Text.Trim();
        var chatId = ChatIdInput.Text.Trim();

        if (!IsValidToken(token))
        {
            SetResult("Enter a valid bot token (looks like 123456:ABC-DEF…).", ok: false);
            return;
        }
        if (!IsValidChatId(chatId))
        {
            SetResult("Enter a valid numeric chat ID (e.g. 123456789).", ok: false);
            return;
        }

        var script = ResolveConfigureScriptPath();
        if (script is null)
        {
            SetResult("configure-telegram.ps1 not found next to the app — reinstall Homebase.", ok: false);
            return;
        }

        SaveApplyButton.IsEnabled = false;
        RecheckButton.IsEnabled = false;
        TestResultText.Foreground = (Brush)FindResource("Brush.InkSoft");
        TestResultText.Text = "Applying — approve the admin prompt…";
        try
        {
            // The one privileged step: elevate to set the machine token, write the chat allowlist,
            // and restart the service. Inputs are format-validated above, so they carry no shell
            // metacharacters. Verb=runas triggers the single UAC prompt.
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
                $"-File \"{script}\" -Token \"{token}\" -ChatId \"{chatId}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                SetResult("Couldn't start the configuration helper.", ok: false);
                return;
            }

            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0)
            {
                TokenInput.Clear();
                SetResult("Saved. The service is restarting — re-checking…", ok: true);
                await Task.Delay(3000); // give the service time to come back up
                Refresh();
            }
            else
            {
                SetResult($"Apply failed (exit {proc.ExitCode}). Double-check the token and chat ID.", ok: false);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetResult("Cancelled — admin approval is needed to save credentials.", ok: false);
        }
        catch (Exception ex)
        {
            SetResult($"Apply failed: {ex.Message}", ok: false);
        }
        finally
        {
            SaveApplyButton.IsEnabled = true;
            RecheckButton.IsEnabled = true;
        }
    }

    private void SetResult(string message, bool ok)
    {
        TestResultText.Foreground = (Brush)FindResource(ok ? "Brush.StatusOnline" : "Brush.StatusBad");
        TestResultText.Text = message;
    }

    // Same formats the installer validates: token "digits:base64ish", chat ID signed integer.
    private static bool IsValidToken(string token) => Regex.IsMatch(token, @"^\d{6,10}:[A-Za-z0-9_-]{30,}$");
    private static bool IsValidChatId(string chatId) => Regex.IsMatch(chatId, @"^-?\d{4,}$");

    // The Tray runs from {app}\Tray; configure-telegram.ps1 is installed one level up at {app}.
    private static string? ResolveConfigureScriptPath()
    {
        var trayDir = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var appDir = System.IO.Directory.GetParent(trayDir)?.FullName;
        if (appDir is null) return null;
        var path = System.IO.Path.Combine(appDir, "configure-telegram.ps1");
        return System.IO.File.Exists(path) ? path : null;
    }

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
