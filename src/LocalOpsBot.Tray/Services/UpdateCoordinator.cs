using System.Net.Http;
using LocalOpsBot.Core.Updates;
using Application = System.Windows.Application;

namespace LocalOpsBot.Tray.Services;

internal enum UpdateOutcome { UpToDate, Declined, Cancelled, Failed, Updating, Busy }

/// <summary>
/// The single owner of the tray's auto-update flow, shared by the tray context menu and the
/// dashboard so both behave identically. Wraps <see cref="UpdateService"/> and drives an
/// <see cref="UpdateProgressWindow"/> for live download progress. Everything here runs on the
/// WPF UI thread; <see cref="Progress{T}"/> callbacks therefore marshal back to it for free.
/// </summary>
internal sealed class UpdateCoordinator
{
    // Same default as claude-usage: long enough to read the notes or press Later, short enough that
    // an unattended PC still picks the update up at startup.
    private const int AutoUpdateCountdownSeconds = 60;

    private readonly UpdateService _updater;
    private bool _busy;

    public UpdateCoordinator()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Homebase.Tray/0.1");
        _updater = new UpdateService(http);
    }

    public string CurrentVersion => _updater.GetCurrentVersionString();

    /// <summary>Silent check used by the startup background poll; returns the newer release or null.</summary>
    public Task<UpdateInfo?> CheckAsync(CancellationToken ct) => _updater.CheckForUpdateAsync(ct);

    /// <summary>
    /// Startup path: a newer release was found in the background, so pop the update dialog —
    /// unless the user already chose "skip this version" for exactly this version, or another
    /// update flow is already open. Must be called on the UI thread.
    /// </summary>
    public async Task<UpdateOutcome> OfferStartupUpdateAsync(UpdateInfo info, IProgress<string>? status = null)
    {
        if (_busy) return UpdateOutcome.Busy;
        if (!ReleaseNotes.ShouldPrompt(info.Version, UpdateVersionMarker.Skipped.Read()))
        {
            status?.Report(L.UpdSkipped(info.Version));
            return UpdateOutcome.Declined;
        }

        // Unprompted offer → count down and install unless the user answers first. Not again for a
        // version whose unattended install already started once and didn't take (see the marker).
        var countdown = ReleaseNotes.ShouldAutoInstall(info.Version, UpdateVersionMarker.AutoAttempted.Read())
            ? AutoUpdateCountdownSeconds
            : 0;

        _busy = true;
        try
        {
            return await OfferAsync(info, status, countdown);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Manual path: check → update dialog → download (with a progress window) → apply (UAC). An
    /// explicit check always shows the dialog, even for a skipped version — the user asked.
    /// <paramref name="status"/> receives short phase strings so the caller can reflect them in its
    /// own affordance (the tray menu item text or the dashboard's update label). Must be called on
    /// the UI thread.
    /// </summary>
    public async Task<UpdateOutcome> RunInteractiveAsync(IProgress<string>? status = null)
    {
        if (_busy) return UpdateOutcome.Busy;
        _busy = true;
        try
        {
            status?.Report(L.UpdChecking);
            UpdateInfo? info;
            try
            {
                info = await _updater.CheckForUpdateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                status?.Report(L.UpdCheckFailed);
                MessageDialog.Show(L.UpdErrorTitle, L.UpdCheckFailedDetail(ex.Message));
                return UpdateOutcome.Failed;
            }

            if (info is null)
            {
                status?.Report(L.UpdUpToDate(CurrentVersion));
                MessageDialog.Show(L.UpdUpToDateTitle, L.UpdUpToDateDetail(CurrentVersion));
                return UpdateOutcome.UpToDate;
            }

            return await OfferAsync(info, status, autoUpdateSeconds: 0);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Shows the release notes with Update / Skip this version / Later and acts on the choice.
    /// <paramref name="autoUpdateSeconds"/> &gt; 0 counts down on the Update button and installs at zero.
    /// </summary>
    private async Task<UpdateOutcome> OfferAsync(UpdateInfo info, IProgress<string>? status, int autoUpdateSeconds)
    {
        switch (UpdateDialog.Show(info, CurrentVersion, autoUpdateSeconds))
        {
            case UpdateDialogChoice.AutoUpdate:
                // Recorded before anything can fail: if this unattended install doesn't take, the next
                // launch offers the same version again and must wait for a click, not loop.
                UpdateVersionMarker.AutoAttempted.Write(info.Version);
                UpdateVersionMarker.Skipped.Write(null);
                return await DownloadAndApplyAsync(info, status);

            case UpdateDialogChoice.Update:
                // Choosing to install supersedes an earlier skip of an older version.
                UpdateVersionMarker.Skipped.Write(null);
                return await DownloadAndApplyAsync(info, status);

            case UpdateDialogChoice.Skip:
                UpdateVersionMarker.Skipped.Write(info.Version);
                status?.Report(L.UpdSkipped(info.Version));
                return UpdateOutcome.Declined;

            default:
                status?.Report(L.UpdAvailableShort(info.Version));
                return UpdateOutcome.Declined;
        }
    }

    private async Task<UpdateOutcome> DownloadAndApplyAsync(UpdateInfo info, IProgress<string>? status)
    {
        var window = new UpdateProgressWindow();
        window.SetStatus(L.UpdDownloading);
        window.Show();
        window.Activate();
        try
        {
            string zip;
            try
            {
                status?.Report(L.UpdDownloadingShort);
                var progress = new Progress<int>(p =>
                {
                    window.SetPercent(p);
                    // The checksum verification runs right after the byte loop hits 100%.
                    if (p >= 100) window.SetStatus(L.UpdVerifying);
                });
                zip = await _updater.DownloadUpdateAsync(info, progress, window.Token);
            }
            catch (OperationCanceledException)
            {
                status?.Report(L.UpdAvailableShort(info.Version));
                return UpdateOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                status?.Report(L.UpdFailed);
                MessageDialog.Show(L.UpdErrorTitle, L.UpdDownloadFailed(ex.Message));
                return UpdateOutcome.Failed;
            }

            // Download + checksum OK. Applying writes Program Files and stops/starts the service,
            // so it needs admin — lock cancellation and launch the elevated helper. Only shut the
            // tray down once that helper is actually running (the user may still decline the UAC).
            window.SetPercent(100);
            window.SetStatus(L.UpdInstalling);
            window.LockCancel();
            // The UAC prompt is next; if the user minimized the download, bring the explanation back.
            window.RestoreAndActivate();
            status?.Report(L.UpdInstallingShort);

            bool started;
            try
            {
                started = _updater.ApplyUpdate(zip, elevate: true);
            }
            catch (Exception ex)
            {
                status?.Report(L.UpdFailed);
                MessageDialog.Show(L.UpdErrorTitle, L.UpdLaunchFailed(ex.Message));
                return UpdateOutcome.Failed;
            }

            if (started)
            {
                // Let the dashboard (which cancels its own close) actually close during shutdown.
                App.IsShuttingDown = true;
                Application.Current.Shutdown();
                return UpdateOutcome.Updating;
            }

            status?.Report(L.UpdAvailableShort(info.Version));
            MessageDialog.Show(L.UpdCancelledTitle, L.UpdCancelledDetail);
            return UpdateOutcome.Cancelled;
        }
        finally
        {
            // The user may have already closed the window (cancel); closing again is a no-op we
            // guard defensively rather than tracking close state.
            try { window.Close(); } catch { /* already closed */ }
        }
    }
}
