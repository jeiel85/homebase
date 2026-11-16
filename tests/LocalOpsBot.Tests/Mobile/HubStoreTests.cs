using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>Hardening #37: an unreadable hubs.json is kept aside instead of being overwritten.</summary>
public class HubStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-hubs-" + Guid.NewGuid().ToString("N"));

    private static LinkedHub Hub(string pcId) => new(pcId, "HUB", "dev1", "token",
        new[] { new MobileEndpoint(MobileTransport.Tailscale, "100.1.2.3", 47820, false) }, DateTimeOffset.UtcNow);

    [Fact]
    public void Links_survive_a_reload()
    {
        new HubStore(_dir, NullLogger.Instance, protect: false).Upsert(Hub("pc-a"));

        var reloaded = new HubStore(_dir, NullLogger.Instance, protect: false);

        Assert.Equal("pc-a", Assert.Single(reloaded.List()).PcId);
    }

    [Fact]
    public void An_unreadable_file_is_moved_aside_before_the_next_save()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "hubs.json");
        File.WriteAllText(path, "{ not json");

        var store = new HubStore(_dir, NullLogger.Instance, protect: false);
        Assert.Empty(store.List());
        store.Upsert(Hub("pc-b"));

        var aside = Assert.Single(Directory.GetFiles(_dir, "hubs.json.corrupt-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
        Assert.Equal("pc-b", Assert.Single(new HubStore(_dir, NullLogger.Instance, protect: false).List()).PcId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
