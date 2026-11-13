using System.IO;
using System.Runtime.Versioning;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Starts the tray-side notification-forwarding pipeline when the feature is enabled in config:
/// listen for Windows toast notifications, filter/mask them, and relay them over the named pipe to
/// the Agent (which bundles them and pushes them encrypted to the phone, DECISIONS M-19). The pipeline types have always existed but were never
/// started from the tray, so forwarding never actually ran — this is the missing wiring. Gated by
/// <see cref="TrayConfig"/>; a disabled feature starts nothing.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class NotificationForwardingHost : IDisposable
{
    private ToastPollingService? _poller;
    private NotificationBridgeClient? _bridge;
    private ServiceProvider? _provider;

    public bool IsRunning => _poller is not null;

    /// <summary>
    /// Starts forwarding if <c>notificationForwarding:enabled</c> is set; a no-op otherwise.
    /// Best-effort — a failure here must never take down the tray.
    /// </summary>
    public void StartIfEnabled()
    {
        FileLogger<ToastPollingService>? logger = null;
        try
        {
            // Route the pipeline's logs to a file so this headless feature is diagnosable.
            var logDir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\Homebase\logs");
            Directory.CreateDirectory(logDir);
            logger = new FileLogger<ToastPollingService>(Path.Combine(logDir, "tray-forwarding.log"));

            var config = TrayConfig.Load();
            var enabled = NotificationForwardingConfig.IsEnabled(config);
            logger.LogInformation("Forwarding host start: enabled={Enabled}", enabled);
            if (!enabled) return;

            // Reuse the Agent's masker construction (default mask patterns) from config.
            var services = new ServiceCollection();
            services.AddLocalOpsNotificationForwarding(config);
            _provider = services.BuildServiceProvider();

            var masker = _provider.GetService<ITextMasker>();
            if (masker is null)
            {
                logger.LogWarning("Text masker not resolved; forwarding not started.");
                return;
            }

            // App filtering: the config block list (password managers, authenticators) always applies;
            // on top of it the live user exclusions (dashboard-editable, applied within a poll cycle,
            // no restart/elevation).
            var filter = new DynamicAppFilter(_provider.GetService<INotificationFilter>());

            var listener = new WindowsToastNotificationListener();
            _bridge = new NotificationBridgeClient();
            _poller = new ToastPollingService(listener, filter, masker, _bridge, logger);

            // BackgroundService.StartAsync kicks off the poll loop (ExecuteAsync) and requests the
            // Windows notification-listener permission on first run.
            logger.LogInformation("Starting ToastPollingService…");
            _poller.StartAsync(CancellationToken.None).ContinueWith(
                t => logger!.LogError(t.Exception, "ToastPollingService.StartAsync faulted"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            // Forwarding is non-essential; never let it break tray startup — but do record why.
            try { logger?.LogError(ex, "Forwarding host failed to start"); } catch { }
        }
    }

    public void Dispose()
    {
        try
        {
            _poller?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // best-effort shutdown
        }
        finally
        {
            _bridge?.Dispose();
            _provider?.Dispose();
        }
    }
}
