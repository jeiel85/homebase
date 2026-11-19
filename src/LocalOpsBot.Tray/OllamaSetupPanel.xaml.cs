using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Tray.Services;

namespace LocalOpsBot.Tray;

/// <summary>
/// Whether the local Ollama server is running with the configured model pulled, and the next step
/// when it isn't: a link to get it, a one-click start (which also registers it to run at login), or
/// the pull command. Used by the setup checklist and by Settings › AI advice — before the Settings
/// area existed these steps were only in the checklist, so after closing it there was no way back to
/// them. Nothing here needs elevation and nothing is written to the Agent config.
/// </summary>
public partial class OllamaSetupPanel : UserControl
{
    private readonly ConnectionReadiness _readiness = new();
    private Task? _probe;
    private string _model = "llama3.2:1b";
    private string _endpoint = "";

    public OllamaSetupPanel()
    {
        InitializeComponent();
    }

    /// <summary>The row's heading (the checklist's "AI advice (Ollama)" by default).</summary>
    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>Also show which model and server the Agent is configured to use.</summary>
    public bool ShowConfig { get; set; }

    /// <summary>The model the Agent is configured to use, as of the last probe.</summary>
    internal string Model => _model;

    /// <summary>Raised on the UI thread after each probe is shown.</summary>
    internal event Action<OllamaProbe>? Probed;

    /// <summary>
    /// Probes Ollama again and shows the result. A probe already under way is shared rather than
    /// doubled, so the tab coming into view and a "re-check" click don't race each other.
    /// </summary>
    internal Task RefreshAsync()
    {
        if (_probe is { IsCompleted: false }) return _probe;
        return _probe = ProbeAsync();
    }

    /// <summary>
    /// Probes after something just changed (the config was written): a probe already under way may
    /// have read the config before the change, so wait it out and probe again rather than share it.
    /// </summary>
    internal async Task RefreshAfterChangeAsync()
    {
        if (_probe is { IsCompleted: false } running) await running; // ProbeAsync never throws
        await RefreshAsync();
    }

    // AI advice is optional and network-bound: a short timeout so a slow or absent Ollama never
    // holds up whatever else the caller is checking.
    private async Task ProbeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            Apply(await _readiness.ProbeOllamaAsync(cts.Token));
        }
        catch
        {
            Apply(new OllamaProbe(OllamaReadiness.Unreachable, _model, _endpoint, null));
        }
    }

    internal void Apply(OllamaProbe p)
    {
        _model = p.Model;
        _endpoint = p.Endpoint;
        InstallHint.Visibility = Visibility.Collapsed;
        StartButton.Visibility = Visibility.Collapsed;
        PullHint.Visibility = Visibility.Collapsed;

        ConfigText.Text = L.OllamaConfig(p.Model, p.Endpoint);
        ConfigText.Visibility = ShowConfig && !string.IsNullOrEmpty(p.Endpoint) ? Visibility.Visible : Visibility.Collapsed;

        switch (p.Status)
        {
            case OllamaReadiness.Ready:
                SetChip("Brush.StatusOnline", L.ChipReady);
                StatusText.Text = L.OllamaReady(p.Model);
                break;
            case OllamaReadiness.ModelMissing:
                SetChip("Brush.StatusWarn", L.ChipSetup);
                StatusText.Text = L.OllamaModelMissing;
                PullCommand.Text = $"ollama pull {p.Model}";
                PullHint.Visibility = Visibility.Visible;
                PullButton.IsEnabled = OllamaSetup.IsValidModelName(p.Model);
                break;
            default: // Unreachable
                SetChip("Brush.Hairline", L.ChipOptional);
                if (OllamaSetup.IsInstalled())
                {
                    // Installed but not running — the common case. One click starts it + autostart.
                    StatusText.Text = L.OllamaNotRunning;
                    StartButton.Visibility = Visibility.Visible;
                }
                else
                {
                    StatusText.Text = L.OllamaNotInstalled;
                    InstallHint.Visibility = Visibility.Visible;
                }
                break;
        }
        Probed?.Invoke(p);
    }

    private void SetChip(string brushKey, string label)
    {
        StateChip.Background = (Brush)FindResource(brushKey);
        StateChipText.Text = label;
    }

    private void Pull_Click(object sender, RoutedEventArgs e)
    {
        if (!OllamaSetup.IsValidModelName(_model))
        {
            StatusText.Text = L.OllamaBadModel;
            return;
        }
        try
        {
            // Open a console running the pull so the user can watch download progress; ollama is
            // a user-level CLI, so this needs no elevation.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k ollama pull {_model}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = L.OllamaPullFailed(ex.Message);
        }
    }

    // One click: register Ollama to run at login (the missing piece) and start it now, then re-probe.
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        StatusText.Text = L.OllamaStarting;
        try
        {
            OllamaSetup.StartAndEnableAutostart();
            await Task.Delay(4000); // give the server a moment to come up before re-probing
            await RefreshAsync();
        }
        finally
        {
            StartButton.IsEnabled = true;
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
}
