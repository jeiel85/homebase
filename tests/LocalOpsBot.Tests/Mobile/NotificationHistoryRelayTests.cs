using LocalOpsBot.Agent.Services;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Tests.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>Relayed notifications land in the PC's notification center (DECISIONS M-32).</summary>
public sealed class NotificationHistoryRelayTests : IAsyncLifetime
{
    private HistoryTestDb _t = null!;
    private readonly HubLinkEndToEndTests.RecordingToaster _toaster = new();

    public async Task InitializeAsync() => _t = await HistoryTestDb.CreateAsync();

    public Task DisposeAsync()
    {
        _t.Dispose();
        return Task.CompletedTask;
    }

    private NotificationForwardingService Forwarding(INotificationHistoryStore history) => new(
        new NullBridge(), new HubLinkEndToEndTests.RecordingRouter(), new StarMasker(),
        new ToastForwardingOptions(60, 150, false), NullLogger<NotificationForwardingService>.Instance,
        TimeProvider.System, new NotificationInbox(TimeProvider.System), _toaster, history);

    [Fact]
    public async Task A_relayed_notification_is_kept_once_masked_with_its_device()
    {
        var forwarding = Forwarding(_t.History);
        var origin = new ToastOrigin("pc-b", "WORK-PC", FromHub: true, Array.Empty<string>(), DeviceKind.Pc);

        await forwarding.AcceptFromDeviceAsync(origin, "Slack", "code 1234", "secret", DateTimeOffset.UtcNow, "n1");
        await forwarding.AcceptFromDeviceAsync(origin, "Slack", "code 1234", "secret", DateTimeOffset.UtcNow, "n1"); // second path

        var entry = Assert.Single((await _t.History.ListAsync(null, 10, CancellationToken.None)).Entries);
        Assert.Equal(HistoryKind.Device, entry.Kind);
        Assert.Equal("WORK-PC", entry.Source);
        Assert.Equal(DeviceKind.Pc, entry.SourceKind);
        Assert.Equal("Slack", entry.App);
        Assert.Equal("*********", entry.Title); // stored as masked, never the raw text
        Assert.Equal("******", entry.Body);
        Assert.Single(_toaster.Snapshot());
    }

    [Fact]
    public async Task A_failing_history_does_not_stop_the_relay()
    {
        var forwarding = Forwarding(new BrokenStore());

        await forwarding.AcceptFromDeviceAsync(new ToastOrigin("p1", "Galaxy", false, Array.Empty<string>()),
            "카카오톡", "엄마", null, DateTimeOffset.UtcNow, "n2");

        Assert.Single(_toaster.Snapshot());
    }

    private sealed class StarMasker : ITextMasker
    {
        public string Mask(string input) => new('*', input.Length);
    }

    private sealed class BrokenStore : INotificationHistoryStore
    {
        public Task AddAsync(DeviceNotificationRecord record, CancellationToken ct) => throw new IOException("disk full");
        public Task<HistoryPage> ListAsync(HistoryCursor? after, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ClearDevicesAsync(LocalOpsBot.Core.Notifications.HistoryClearScope? scope, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteDeviceAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> PruneAsync(DateTimeOffset cutoff, int maxDeviceEntries, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NullBridge : INotificationBridgeServer
    {
#pragma warning disable CS0067
        public event Action<ToastNotificationEvent>? NotificationReceived;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
