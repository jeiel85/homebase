using System.ComponentModel;
using System.Windows;

namespace LocalOpsBot.Tray;

/// <summary>
/// A small themed window that shows live download progress during an auto-update. The
/// <see cref="UpdateCoordinator"/> drives it: it reports bytes via <see cref="SetPercent"/> and
/// phase text via <see cref="SetStatus"/>. Closing the window (Cancel or the X) cancels
/// <see cref="Token"/> so the in-flight download stops.
/// </summary>
public partial class UpdateProgressWindow : ThemedWindow
{
    private readonly CancellationTokenSource _cts = new();

    public UpdateProgressWindow() => InitializeComponent();

    /// <summary>Cancelled when the user dismisses the window; link the download to it.</summary>
    public CancellationToken Token => _cts.Token;

    public void SetStatus(string status) => StatusText.Text = status;

    public void SetPercent(int percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        ProgressBar.Value = p;
        PercentText.Text = $"{p}%";
    }

    /// <summary>Disable cancellation once the download is done and the elevated apply is launching.</summary>
    public void LockCancel() => CancelButton.IsEnabled = false;

    /// <summary>Un-minimizes the window (the user may have set the download aside) and brings it forward.</summary>
    public void RestoreAndActivate()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing while a download is running should stop it; harmless once it has completed.
        _cts.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Dispose();
        base.OnClosed(e);
    }
}
