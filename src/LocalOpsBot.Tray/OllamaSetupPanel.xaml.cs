using System.Diagnostics;
using System.Net.Http;
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
    private CancellationTokenSource? _pullCancellation;

    public OllamaSetupPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => _pullCancellation?.Cancel();
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
        if (string.IsNullOrWhiteSpace(PullModelText.Text) || PullModelText.Text == _model)
            PullModelText.Text = p.Model;
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
                StatusText.Text = ShowConfig && p.Installed.Count > 0
                    ? L.OllamaChooseInstalled : L.OllamaModelMissing;
                PullHint.Visibility = !ShowConfig || p.Installed.Count == 0 || _pullCancellation is not null
                    ? Visibility.Visible : Visibility.Collapsed;
                PullModelText.IsReadOnly = !ShowConfig;
                DownloadHintText.Text = ShowConfig ? L.OllamaDownloadHint : L.OllamaDownloadConfiguredHint;
                UpdatePullButton();
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
        if (_pullCancellation is not null) PullHint.Visibility = Visibility.Visible;
        Probed?.Invoke(p);
    }

    private void SetChip(string brushKey, string label)
    {
        StateChip.Background = (Brush)FindResource(brushKey);
        StateChipText.Text = label;
    }

    private void PullModel_TextChanged(object sender, TextChangedEventArgs e) => UpdatePullButton();

    private void UpdatePullButton()
    {
        if (PullButton is not null)
            PullButton.IsEnabled = _pullCancellation is null && OllamaSetup.IsValidModelName(PullModelText.Text.Trim());
    }

    private void CancelPull_Click(object sender, RoutedEventArgs e) => _pullCancellation?.Cancel();

    private async void Pull_Click(object sender, RoutedEventArgs e) => await DownloadAsync();

    internal async Task DownloadAsync()
    {
        if (_pullCancellation is not null) return;
        var model = PullModelText.Text.Trim();
        if (!OllamaSetup.IsValidModelName(model))
        {
            StatusText.Text = L.OllamaBadModel;
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _pullCancellation = cancellation;
        UpdatePullButton();
        PullModelText.IsEnabled = false;
        CancelPullButton.Visibility = Visibility.Visible;
        PullProgressBar.Visibility = Visibility.Visible;
        PullProgressBar.IsIndeterminate = true;
        PullStatusText.Text = L.OllamaDownloading;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var progress = new Progress<OllamaPullProgress>(p =>
            {
                if (_pullCancellation != cancellation) return;
                PullProgressBar.IsIndeterminate = p.Total <= 0;
                PullProgressBar.Value = p.Total > 0 ? Math.Clamp(100d * p.Completed / p.Total, 0, 100) : 0;
                PullStatusText.Text = p.Total > 0
                    ? L.OllamaDownloadProgress(PullProgressBar.Value, p.Completed / 1_000_000d, p.Total / 1_000_000d)
                    : L.OllamaDownloading;
            });
            await new OllamaModelDownloader(http).PullAsync(_endpoint, model, progress, cancellation.Token);
            _pullCancellation = null; // queued progress must not overwrite the completed state
            PullStatusText.Text = L.OllamaDownloadDone;
            await RefreshAfterChangeAsync();
        }
        catch (OperationCanceledException)
        {
            PullStatusText.Text = L.OllamaDownloadCancelled;
        }
        catch (Exception ex)
        {
            PullStatusText.Text = L.OllamaPullFailed(ex.Message);
        }
        finally
        {
            _pullCancellation = null;
            PullModelText.IsEnabled = true;
            CancelPullButton.Visibility = Visibility.Collapsed;
            PullProgressBar.Visibility = Visibility.Collapsed;
            UpdatePullButton();
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
