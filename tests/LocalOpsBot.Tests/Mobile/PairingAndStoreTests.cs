using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Infrastructure.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

public class PairingServiceTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new();

    [Fact]
    public void Valid_code_pairs_once_and_stores_only_the_token_hash()
    {
        var svc = new PairingService(_store, _time);
        var (code, _) = svc.Begin();

        var result = svc.TryPair(code, "Galaxy S24", "SM-S921N");

        Assert.Equal(PairOutcome.Paired, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.NotNull(_store.Authenticate(result.Token!));
        Assert.Equal(PairOutcome.InvalidCode, svc.TryPair(code, "again", null).Outcome); // single use
    }

    [Fact]
    public void Expired_code_is_rejected()
    {
        var svc = new PairingService(_store, _time);
        var (code, _) = svc.Begin();
        _time.Advance(PairingService.CodeLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(PairOutcome.Expired, svc.TryPair(code, "x", null).Outcome);
        Assert.Empty(_store.List());
    }

    [Fact]
    public void New_offer_replaces_the_previous_code()
    {
        var svc = new PairingService(_store, _time);
        var (first, _) = svc.Begin();
        svc.Begin();
        Assert.Equal(PairOutcome.InvalidCode, svc.TryPair(first, "x", null).Outcome);
    }

    [Fact]
    public void Repeated_wrong_codes_lock_pairing_until_the_window_passes()
    {
        var svc = new PairingService(_store, _time);
        var (code, _) = svc.Begin();
        for (var i = 0; i < PairingService.MaxFailures; i++) svc.TryPair("wrong", "x", null);

        Assert.Equal(PairOutcome.Locked, svc.TryPair(code, "x", null).Outcome);

        _time.Advance(PairingService.FailureWindow + TimeSpan.FromSeconds(1));
        var (fresh, _) = svc.Begin();
        Assert.Equal(PairOutcome.Paired, svc.TryPair(fresh, "x", null).Outcome);
    }

    [Fact]
    public void Device_name_is_trimmed_and_defaulted()
    {
        var svc = new PairingService(_store, _time);
        var (code, _) = svc.Begin();
        svc.TryPair(code, "   ", null);
        Assert.Equal("Android", _store.List().Single().Name);
    }
}

public class MobileDeviceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-mobile-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void Pc_id_and_devices_survive_reload_and_tokens_are_not_stored_in_clear()
    {
        var store = new MobileDeviceStore(_dir, _time, restrictAcl: false);
        const string token = "secret-token-value";
        var added = store.Add("Phone", "Pixel", PairingService.HashToken(token));

        var reloaded = new MobileDeviceStore(_dir, _time, restrictAcl: false);
        Assert.Equal(store.PcId, reloaded.PcId);
        Assert.Equal(added.DeviceId, reloaded.Authenticate(token)?.DeviceId);
        Assert.Null(reloaded.Authenticate("other"));
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(_dir, "state.json")));
    }

    [Fact]
    public void Revoked_device_can_no_longer_authenticate()
    {
        var store = new MobileDeviceStore(_dir, _time, restrictAcl: false);
        var d = store.Add("Phone", null, PairingService.HashToken("t"));
        Assert.True(store.Revoke(d.DeviceId));
        Assert.Null(store.Authenticate("t"));
        Assert.False(store.Revoke(d.DeviceId));
    }

    private static LocalOpsBot.Core.Push.PushSubscription Sub(string endpoint) =>
        new(endpoint, "p256dh", "auth", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Re_pairing_the_same_phone_removes_its_earlier_entry()
    {
        var store = new MobileDeviceStore(_dir, _time, restrictAcl: false);
        var old = store.Add("S24", null, PairingService.HashToken("old"));
        store.SetPushSubscription(old.DeviceId, Sub("https://ntfy.sh/upS24?up=1"));
        var tablet = store.Add("Tab", null, PairingService.HashToken("tab"));
        store.SetPushSubscription(tablet.DeviceId, Sub("https://ntfy.sh/upTab?up=1"));

        _time.Advance(TimeSpan.FromHours(12));
        var again = store.Add("S24", null, PairingService.HashToken("new"));
        Assert.Equal(3, store.List().Count); // not yet known to be the same phone
        store.SetPushSubscription(again.DeviceId, Sub("https://ntfy.sh/upS24?up=1"));

        Assert.Equal(new[] { tablet.DeviceId, again.DeviceId }.Order(), store.List().Select(d => d.DeviceId).Order());
        Assert.Null(store.Authenticate("old"));
        Assert.Equal(again.DeviceId, store.Authenticate("new")?.DeviceId);
        Assert.Equal(2, store.PushTargets().Count); // one push per phone
        Assert.Equal(2, new MobileDeviceStore(_dir, _time, restrictAcl: false).List().Count); // persisted
    }

    [Fact]
    public void Duplicates_left_by_an_older_version_are_cleaned_up_on_load_keeping_the_newest()
    {
        var store = new MobileDeviceStore(_dir, _time, restrictAcl: false);
        var old = store.Add("S24", null, PairingService.HashToken("old"));
        store.SetPushSubscription(old.DeviceId, Sub("https://ntfy.sh/upS24?up=1"));
        _time.Advance(TimeSpan.FromHours(12));
        var again = store.Add("S24", null, PairingService.HashToken("new"));
        store.SetPushSubscription(again.DeviceId, Sub("https://ntfy.sh/upPLACEHOLDER?up=1"));
        // What v0.18.0 and earlier left behind: two entries with one endpoint.
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("upPLACEHOLDER", "upS24"));

        var reloaded = new MobileDeviceStore(_dir, _time, restrictAcl: false);

        var kept = Assert.Single(reloaded.List());
        Assert.Equal(again.DeviceId, kept.DeviceId);
        Assert.DoesNotContain(old.DeviceId, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{not json")]
    public void Corrupt_state_file_is_kept_aside_and_does_not_stop_the_agent(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "state.json"), content);

        var store = new MobileDeviceStore(_dir, _time, restrictAcl: false);

        Assert.Empty(store.List());
        Assert.False(string.IsNullOrEmpty(store.PcId));
        var aside = Assert.Single(Directory.GetFiles(_dir, "state.json.corrupt-*"));
        Assert.Equal(content, File.ReadAllText(aside)); // original bytes preserved for inspection
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
