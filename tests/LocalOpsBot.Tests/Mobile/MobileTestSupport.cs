using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tests.Mobile;

internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class InMemoryDeviceStore : IMobileDeviceStore
{
    private readonly List<(MobileDeviceInfo Info, string Hash)> _devices = new();
    public string PcId => "pc-test";
    public IReadOnlyList<MobileDeviceInfo> List() => _devices.Select(d => d.Info).ToList();

    public MobileDeviceInfo Add(string name, string? model, string tokenSha256Hex)
    {
        var info = new MobileDeviceInfo($"dev{_devices.Count + 1}", name, model, DateTimeOffset.UnixEpoch, null);
        _devices.Add((info, tokenSha256Hex));
        return info;
    }

    public bool Revoke(string deviceId) => _devices.RemoveAll(d => d.Info.DeviceId == deviceId) > 0;

    public MobileDeviceInfo? Authenticate(string token)
    {
        var h = PairingService.HashToken(token);
        return _devices.Where(d => d.Hash == h).Select(d => d.Info).FirstOrDefault();
    }
}

internal sealed class FakeSampler : IResourceSampler
{
    public bool Started { get; private set; }
    public int StartCount { get; private set; }
    public bool NetPerProcess => Started;
    public List<ProcessSample> Processes { get; } = new();
    public void Start() { Started = true; StartCount++; }
    public void Stop() => Started = false;
    public (IReadOnlyList<ProcessSample> Processes, SystemTotals System) Sample() =>
        (Processes.ToList(), new SystemTotals(12.5, 4_000, 16_000, 100, 10, 20, 3600));
}
