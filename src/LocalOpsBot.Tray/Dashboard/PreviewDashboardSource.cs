using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Dashboard;

/// <summary>
/// Made-up devices for <c>Homebase.Tray.exe --preview-dashboard</c>: lets the dashboard be designed and
/// checked without an Agent, linked PCs or phones. Never used by the tray icon.
/// </summary>
internal sealed class PreviewDashboardSource : IDashboardSource
{
    private readonly Random _rng = new(7);
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private double _cpu = 18, _mem = 52, _gpu = 31, _ping = 9;

    public bool Connected => true;
    public bool FeatureEnabled => true;
    public bool LiveUnsupported => false;

    public Task<DeviceOverview?> OverviewAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var phone = new DeviceStatus("0.33.0", Battery: 76, Charging: true, Network: "wifi", MemUsedBytes: 7_301_444_403,
            MemTotalBytes: 11_811_160_064, UptimeSec: 259_200, Os: "Android 15", StorageUsedBytes: 143_881_404_416,
            StorageTotalBytes: 255_550_554_112, BatteryTempC: 31.5, BatteryHealth: "good", Plug: "usb", ChargeFullSec: 2_100,
            PowerSave: false, Thermal: "none", Signal: 3, LinkMbps: 866, Forwarding: true, Dnd: false);
        var here = new DevicesResponse(1,
            new HubEntry("pc-self", Environment.MachineName, "0.33.0", new DeviceStatus("0.33.0", CpuPct: _cpu, MemUsedBytes: 17_000_000_000,
                MemTotalBytes: 34_359_738_368, UptimeSec: 93_784, Os: "Windows 11 Pro 24H2")),
            new[]
            {
                new DeviceEntry("dev-laptop", "LAPTOP-JEIEL", DeviceKind.Pc, "Windows", now.AddSeconds(-12), true,
                    new DeviceStatus("0.33.0", CpuPct: 9, MemUsedBytes: 9_100_000_000, MemTotalBytes: 16_000_000_000, UptimeSec: 7_400,
                        Os: "Windows 11 Home 24H2"), PcId: "pc-laptop"),
                new DeviceEntry("dev-s24", "Galaxy S24", DeviceKind.Phone, "samsung SM-S921N", now.AddSeconds(-40), true, phone),
                new DeviceEntry("dev-tab", "Galaxy Tab S9", DeviceKind.Phone, "samsung SM-X710", now.AddHours(-5), false,
                    new DeviceStatus("0.31.0", Battery: 41, Charging: false, Network: "wifi")),
            },
            null);
        var hub = new LinkedHubView("pc-work", "WORK-PC", true, now.AddSeconds(-8),
            new DeviceStatus("0.33.0", CpuPct: 34, MemUsedBytes: 21_000_000_000, MemTotalBytes: 32_000_000_000, UptimeSec: 400_000,
                Os: "Windows 11 Pro 23H2"),
            new[]
            {
                new DeviceEntry("w-self", Environment.MachineName, DeviceKind.Pc, "Windows", now, true, null),
                new DeviceEntry("w-pixel", "Pixel 8", DeviceKind.Phone, "Google Pixel 8", now.AddMinutes(-3), true,
                    new DeviceStatus("0.33.0", Battery: 18, Charging: false, Network: "cellular", Os: "Android 16", Signal: 2,
                        MemUsedBytes: 5_100_000_000, MemTotalBytes: 8_000_000_000, StorageUsedBytes: 98_000_000_000,
                        StorageTotalBytes: 128_000_000_000, BatteryTempC: 36.2, PowerSave: true, Thermal: "light")),
            },
            null, "w-self");
        var local = new[]
        {
            new LocalDeviceInfo("dev-s24", now.AddDays(-3), true, new[] { "advisor", "event_log" }),
            new LocalDeviceInfo("dev-tab", now.AddDays(-20), false, null),
            new LocalDeviceInfo("dev-laptop", now.AddDays(-2), false, null),
        };
        return Task.FromResult<DeviceOverview?>(new DeviceOverview(here, new[] { hub }, local));
    }

    public Task<LiveView?> LiveAsync(string target)
    {
        Walk();
        var now = DateTimeOffset.UtcNow;
        if (target == LiveTarget.Device("dev-laptop") && now - _started < TimeSpan.FromSeconds(3))
            return Task.FromResult<LiveView?>(new LiveView(LiveViewState.Connecting, Via: "shared"));
        var name = target == LiveTarget.Self ? Environment.MachineName : target.Contains("work") ? "WORK-PC" : "LAPTOP-JEIEL";
        var via = target == LiveTarget.Self ? "self" : target.StartsWith("hub:", StringComparison.Ordinal) ? MobileTransport.Lan : "shared";
        return Task.FromResult<LiveView?>(new LiveView(LiveViewState.Live, Snapshot(name, now), via, At: now));
    }

    private void Walk()
    {
        double Step(double v, double span, double lo, double hi) => Math.Clamp(v + (_rng.NextDouble() - 0.5) * span, lo, hi);
        _cpu = Step(_cpu, 14, 3, 96);
        _mem = Step(_mem, 2, 35, 80);
        _gpu = Step(_gpu, 18, 0, 99);
        _ping = Step(_ping, 4, 5, 40);
    }

    private LiveSnapshot Snapshot(string name, DateTimeOffset now)
    {
        const long total = 34_359_738_368;
        var sys = new SystemTotals(_cpu, (long)(total * _mem / 100), total, _rng.Next(0, 6_000_000), _rng.Next(20_000, 2_400_000),
            _rng.Next(5_000, 400_000), 93_784 + (long)(now - _started).TotalSeconds);
        AppEntry App(int pid, string process, string title, double cpu, long mem, bool fg = false, int windows = 1) => new(pid, process, fg,
            Enumerable.Range(0, windows).Select(i => new WindowTitle(i == 0 ? title : $"{title} ({i + 1})", fg && i == 0)).ToList(),
            new ResourceUsage(cpu * (0.6 + _rng.NextDouble() * 0.8), mem, _rng.Next(0, 300_000), _rng.Next(0, 90_000), _rng.Next(0, 20_000)),
            Array.Empty<ProcessEntry>());
        var apps = new[]
        {
            App(15816, "WindowsTerminal", "✳ Homebase 기기 화면", 12.5, 734_003_200, fg: true),
            App(33052, "chrome", "Homebase v0.33.0 릴리스 - Google Chrome", 6.1, 2_200_000_000, windows: 3),
            App(4120, "Code", "DashboardWindow.xaml - homebase - Visual Studio Code", 3.4, 1_100_000_000),
            App(9932, "KakaoTalk", "카카오톡", 0.4, 310_000_000),
            App(2210, "slack", "Slack | homebase | OKPOS", 1.2, 640_000_000),
            App(7781, "Claude", "Claude", 0.9, 520_000_000),
        };
        var procs = new[]
        {
            new ProcessEntry(20480, 15816, "node", new ResourceUsage(9.8, 600_000_000, 4096, 2048, 1024)),
            new ProcessEntry(4040, 1, "MsMpEng", new ResourceUsage(1.7, 310_000_000, 120_000, null, null)),
            new ProcessEntry(5120, 1, "Homebase.Agent", new ResourceUsage(0.8, 96_000_000, 4096, 6_000, 3_000)),
            new ProcessEntry(812, 1, "svchost", new ResourceUsage(0.3, 42_000_000, 0, null, null)),
            new ProcessEntry(6600, 1, "ollama", new ResourceUsage(0.1, 1_400_000_000, 0, null, null)),
        };
        var work = new[]
        {
            new WorkItem(WorkKind.Agent, "Claude Code", "PC에서 연결된 기기 정보 보기", @"D:\Project\homebase", now.AddMinutes(-1),
                Agent: new AgentInfo(AgentStatus.Waiting, "도구 사용 승인", now.AddMinutes(-4), "Opus 5.5")),
            new WorkItem(WorkKind.Agent, "Codex", "OpenCode에 모델 추가", @"C:\Users\me\Documents\Codex", now.AddMinutes(-7)),
            new WorkItem(WorkKind.Editor, "VS Code", "DashboardWindow.xaml", "homebase", Foreground: true),
            new WorkItem(WorkKind.Document, "Word", "견적서.docx"),
            new WorkItem(WorkKind.Browser, "Chrome", "Homebase v0.33.0 릴리스"),
        };
        var hw = new HardwareInfo("AMD Ryzen 7 250 w/ Radeon 780M Graphics", 55 + _cpu / 4, null,
            new[]
            {
                new GpuReading("NVIDIA GeForce RTX 5060 Laptop GPU", "nvidia", false, _gpu, 48 + _gpu / 5, 89, 1_492_123_648, 8_546_942_976,
                    338_051_072, 8_217_880_576, 18 + _gpu / 2, 95, null, 32, null, 1_905, 7_001, "nvidia-smi+windows"),
                new GpuReading("AMD Radeon 780M Graphics", "amd", true, 4, 53, null, 379_809_792, 536_870_912,
                    355_807_232, 8_217_880_576, null, null, 3.2, null, null, null, 1_600, "windows"),
            },
            new[] { new DiskUsage("C:", 510_891_388_928, 327_434_858_496), new DiskUsage("D:", 1_447_034_875_904, 1_400_076_177_408) },
            new PingStats("1.1.1.1", _ping, 10.3, 0, 30));
        return new LiveSnapshot(1, now, name, sys, true, true, apps, procs, Array.Empty<ClaudeSession>(), work, hw);
    }
}
