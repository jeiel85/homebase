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

    /// <summary>For tests: an updater over a fake HTTP handler.</summary>
    internal UpdateCoordinator(UpdateService updater) => _updater = updater;

    public string CurrentVersion => _updater.GetCurrentVersionString();

    /// <summary>An update flow (dialog, download or install) is open; a new one would return <see cref="UpdateOutcome.Busy"/>.</summary>
    public bool IsBusy => _busy;

    /// <summary>Silent check used by the startup and daily background checks; returns the newer release or null.</summary>
    public Task<UpdateInfo?> CheckAsync(CancellationToken ct) => _updater.CheckForUpdateAsync(ct);

    /// <summary>The menu/label text for a release found in the background: "skipped" when the user skipped exactly it.</summary>
    public static string AvailableLabel(UpdateInfo info) =>
        ReleaseNotes.ShouldPrompt(info.Version, UpdateVersionMarker.Skipped.Read())
            ? L.MenuUpdateAvailable(info.Version)
            : L.UpdSkipped(info.Version);

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
            TrayLog.Info("Update", $"v{info.Version} available; skipped by the user, not offered");
            status?.Report(L.UpdSkipped(info.Version));
            return UpdateOutcome.Declined;
        }

        // Unprompted offer → count down and install unless the user answers first. Not again for a
        // version whose unattended install already started once and didn't take (see the marker).
        var countdown = ReleaseNotes.ShouldAutoInstall(info.Version, UpdateVersionMarker.AutoAttempted.Read())
            ? AutoUpdateCountdownSeconds
            : 0;
        TrayLog.Info("Update", $"Offering v{info.Version} at startup (countdown {countdown} s)");

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
        if (_busy)
        {
            ShowBusy();
            return UpdateOutcome.Busy;
        }
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
                TrayLog.Warn("Update", $"Manual update check failed: {ex.Message}");
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
    /// "Check for updates" while another update flow is open used to do nothing visible (issue #10):
    /// bring that flow's window forward instead, or — while it is still checking and has no window
    /// yet — say so.
    /// </summary>
    private static void ShowBusy()
    {
        var open = Application.Current?.Windows.OfType<System.Windows.Window>()
            .FirstOrDefault(w => (w is UpdateDialog || w is UpdateProgressWindow) && w.IsVisible);
        if (open is null)
        {
            MessageDialog.Show(L.UpdBusyTitle, L.UpdBusyDetail);
            return;
        }
        if (open.WindowState == System.Windows.WindowState.Minimized)
            open.WindowState = System.Windows.WindowState.Normal;
        open.Activate();
    }

    /// <summary>
    /// Shows the release notes with Update / Skip this version / Later and acts on the choice.
    /// <paramref name="autoUpdateSeconds"/> &gt; 0 counts down on the Update button and installs at zero.
    /// </summary>
    private async Task<UpdateOutcome> OfferAsync(UpdateInfo info, IProgress<string>? status, int autoUpdateSeconds)
    {
        // If an unattended install of this very version already stopped somewhere, say where.
        var choice = UpdateDialog.Show(info, CurrentVersion, autoUpdateSeconds, UpdateAutoOutcome.NoteFor(info.Version));
        TrayLog.Info("Update", $"Update dialog for v{info.Version}: {choice}");
        switch (choice)
        {
            case UpdateDialogChoice.AutoUpdate:
                // Recorded before anything can fail: if this unattended install doesn't take, the next
                // launch offers the same version again and must wait for a click, not loop.
                UpdateVersionMarker.AutoAttempted.Write(info.Version);
                UpdateVersionMarker.Skipped.Write(null);
                return await DownloadAndApplyAsync(info, status, unattended: true);

            case UpdateDialogChoice.Update:
                // Choosing to install supersedes an earlier skip of an older version.
                UpdateVersionMarker.Skipped.Write(null);
                return await DownloadAndApplyAsync(info, status, unattended: false);

            case UpdateDialogChoice.Skip:
                UpdateVersionMarker.Skipped.Write(info.Version);
                status?.Report(L.UpdSkipped(info.Version));
                return UpdateOutcome.Declined;

            default:
                status?.Report(L.UpdAvailableShort(info.Version));
                return UpdateOutcome.Declined;
        }
    }

    /// <summary>
    /// Downloads, verifies and launches the elevated installer. <paramref name="unattended"/> = the
    /// startup countdown started it, so nobody may be watching: each way it can end is recorded
    /// (<see cref="UpdateAutoOutcome"/>) for tray.log and the next update popup (issue #18).
    /// </summary>
    internal async Task<UpdateOutcome> DownloadAndApplyAsync(UpdateInfo info, IProgress<string>? status, bool unattended)
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
                if (unattended) UpdateAutoOutcome.Write(info.Version, AutoInstallResult.Cancelled);
                status?.Report(L.UpdAvailableShort(info.Version));
                return UpdateOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                TrayLog.Warn("Update", $"Download of v{info.Version} failed: {ex.Message}");
                if (unattended) UpdateAutoOutcome.Write(info.Version, AutoInstallResult.Failed);
                status?.Report(L.UpdFailed);
                CloseThenShow(L.UpdErrorTitle, L.UpdDownloadFailed(ex.Message));
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

            // Written before the prompt: if nobody answers and the session ends while it waits, the
            // next launch still knows the install stalled here.
            if (unattended) UpdateAutoOutcome.Write(info.Version, AutoInstallResult.AwaitingApproval);

            bool started;
            try
            {
                started = _updater.ApplyUpdate(zip, elevate: true);
            }
            catch (Exception ex)
            {
                TrayLog.Warn("Update", $"Launching the installer for v{info.Version} failed: {ex.Message}");
                if (unattended) UpdateAutoOutcome.Write(info.Version, AutoInstallResult.Failed);
                status?.Report(L.UpdFailed);
                CloseThenShow(L.UpdErrorTitle, L.UpdLaunchFailed(ex.Message));
                return UpdateOutcome.Failed;
            }

            if (started)
            {
                if (unattended) UpdateAutoOutcome.Write(info.Version, AutoInstallResult.Started);
                else TrayLog.Info("Update", $"Installer for v{info.Version} started");
                // Let the dashboard (which cancels its own close) actually close during shutdown.
                App.IsShuttingDown = true;
                Application.Current.Shutdown();
                return UpdateOutcome.Updating;
            }

            // ERROR_CANCELLED: the administrator prompt was declined or dismissed without an answer.
            if (unattended) UpdateAutoOutcome.Write(info.Version, AutoInstallResult.NotApproved);
            else TrayLog.Info("Update", $"Installer for v{info.Version} not approved at the administrator prompt");
            status?.Report(L.UpdAvailableShort(info.Version));
            CloseThenShow(L.UpdCancelledTitle, L.UpdCancelledDetail);
            return UpdateOutcome.Cancelled;
        }
        finally
        {
            CloseQuietly(window);
        }

        // The result notice must replace the progress window, not sit under it: shown while the
        // window was still open, the modal notice opened behind it and disabled it, so the window
        // stayed on "Installing… approve the administrator prompt" at 100% with Cancel and X dead.
        //
        // An unattended install (the startup countdown ran out) shows no notice at all: nobody may be
        // there to close it, and until it is closed this flow stays busy, so the daily check and
        // "Check for updates" would only answer Busy (#71). Its outcome is already recorded
        // (UpdateAutoOutcome) and the next update popup says where it stopped.
        void CloseThenShow(string title, string detail)
        {
            CloseQuietly(window);
            if (unattended)
            {
                TrayLog.Info("Update", $"Unattended install of v{info.Version}: no notice shown ({title}); the next update popup tells");
                return;
            }
            TrayLog.Info("Update", $"Result notice shown: {title}");
            MessageDialog.Show(title, detail);
            TrayLog.Info("Update", $"Result notice closed: {title}");
        }
    }

    private static void CloseQuietly(System.Windows.Window window)
    {
        // The user may have already closed it (cancel); closing again is guarded rather than
        // tracking close state.
        try { window.Close(); } catch { /* already closed */ }
    }
}
