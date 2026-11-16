using System.Net.NetworkInformation;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>
/// The live view's hardware panel (DECISIONS M-34): merging GPU sources, nvidia-smi lines, the ping window,
/// temperature picking, which adapters count toward network totals, and the hub's start/stop of the sampler.
/// GPU cases follow PhoneBox's tests (MIT), whose merging rules this port keeps.
/// </summary>
public class HardwareTests
{
    private static GpuReading G(string name, bool integrated = false, long? vram = null, string? source = null, double? load = null) =>
        new(name, null, integrated, load, null, null, null, vram, null, null, null, null, null, null, null, null, null, source);

    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void The_discrete_card_comes_first_even_when_the_integrated_one_is_listed_first()
    {
        var list = GpuSources.Compose(null, [G("AMD Radeon(TM) Graphics", integrated: true, vram: 2 * GiB), G("NVIDIA GeForce RTX 5080", vram: 16 * GiB)]);
        Assert.Equal(["NVIDIA GeForce RTX 5080", "AMD Radeon(TM) Graphics"], list.Select(g => g.Name));
    }

    [Fact]
    public void Among_discrete_cards_the_one_with_more_video_memory_comes_first()
    {
        var list = GpuSources.Compose([G("NVIDIA GeForce RTX 3060", vram: 12 * GiB), G("NVIDIA GeForce RTX 4090", vram: 24 * GiB)]);
        Assert.Equal("NVIDIA GeForce RTX 4090", list[0].Name);
    }

    [Fact]
    public void The_same_card_from_two_sources_is_one_entry_and_the_first_source_wins()
    {
        var smi = new GpuReading("NVIDIA GeForce RTX 5080", "nvidia", false, 3, 42.3, null, 3 * GiB, 16 * GiB, null, null,
            40.7, 360, null, 0, null, 210, 15_001, "nvidia-smi");
        var os = new GpuReading("NVIDIA GeForce RTX 5080", "nvidia", false, 5, 42.1, 88, 3 * GiB + 1, 15 * GiB, GiB / 4, 100 * GiB,
            null, null, 11, null, 1_200, null, 15_000, "windows");
        var g = Assert.Single(GpuSources.Compose([smi], [os]));
        Assert.Equal(3, g.LoadPct);           // nvidia-smi's
        Assert.Equal(16 * GiB, g.VramTotalBytes);
        Assert.Equal(GiB / 4, g.SharedUsedBytes); // only Windows knows these
        Assert.Equal(88, g.TempMaxC);
        Assert.Equal(11, g.PowerPct);
        Assert.Equal(1_200, g.FanRpm);
        Assert.Equal("nvidia-smi+windows", g.Source);
    }

    [Fact]
    public void Names_match_without_trademark_marks_and_punctuation()
    {
        var g = Assert.Single(GpuSources.Compose([G("AMD Radeon(TM) Graphics", source: "a")], [G("AMD Radeon Graphics", source: "b", load: 7)]));
        Assert.Equal(7, g.LoadPct);
        Assert.Equal("a+b", g.Source);
    }

    [Fact]
    public void Two_identical_cards_stay_two_entries()
    {
        var list = GpuSources.Compose(
            [G("NVIDIA GeForce RTX 3090", vram: 24 * GiB, source: "nvidia-smi"), G("NVIDIA GeForce RTX 3090", vram: 24 * GiB, source: "nvidia-smi")],
            [G("NVIDIA GeForce RTX 3090", vram: 24 * GiB, source: "windows"), G("NVIDIA GeForce RTX 3090", vram: 24 * GiB, source: "windows")]);
        Assert.Equal(2, list.Count);
        Assert.All(list, g => Assert.Equal("nvidia-smi+windows", g.Source));
    }

    [Fact]
    public void Without_nvidia_smi_the_windows_entries_stay()
    {
        var list = GpuSources.Compose(null, [G("AMD Radeon RX 7900 XTX", vram: 24 * GiB)]);
        Assert.Equal("AMD Radeon RX 7900 XTX", Assert.Single(list).Name);
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon 780M Graphics", true)]
    [InlineData("AMD Radeon(TM) RX Vega 11 Graphics", true)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData("AMD Radeon RX 6600 Graphics", false)]
    [InlineData("AMD Radeon Pro W7900", false)]
    [InlineData("Intel(R) UHD Graphics 770", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("Intel(R) Arc(TM) Graphics", true)]
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)", true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", false)]
    [InlineData("NVIDIA GeForce RTX 5060 Laptop GPU", false)]
    public void Integrated_graphics_are_told_apart_by_name(string name, bool integrated) =>
        Assert.Equal(integrated, GpuSources.LooksIntegrated(name));

    [Theory]
    [InlineData(0x10deu, "nvidia")]
    [InlineData(0x1002u, "amd")]
    [InlineData(0x8086u, "intel")]
    [InlineData(0x1414u, null)]
    public void Pci_vendor_ids_name_the_vendor(uint id, string? vendor) => Assert.Equal(vendor, GpuSources.VendorOf(id));

    [Fact]
    public void Nvidia_smi_lines_carry_the_index_name_memory_in_bytes_and_clocks()
    {
        var names = GpuSources.ParseNvidiaNames("0, NVIDIA GeForce RTX 5080\r\n1, NVIDIA GeForce RTX 3060\r\n");
        var row = GpuSources.ParseNvidiaLine("1, 37, 61, 120.5, 170.00, 2048, 12288, 30, 1800, 7501", names);
        Assert.NotNull(row);
        Assert.Equal(1, row!.Value.Index);
        var g = row.Value.Gpu;
        Assert.Equal("NVIDIA GeForce RTX 3060", g.Name);
        Assert.Equal(37, g.LoadPct);
        Assert.Equal(61, g.TempC);
        Assert.Equal(120.5, g.PowerW);
        Assert.Equal(170, g.PowerLimitW);
        Assert.Equal(2 * GiB, g.VramUsedBytes);
        Assert.Equal(12 * GiB, g.VramTotalBytes);
        Assert.Equal(30, g.FanPct);
        Assert.Equal(1800, g.ClockMhz);
        Assert.Equal(7501, g.MemClockMhz);
        Assert.Equal("nvidia", g.Vendor);
        Assert.False(g.Integrated);
    }

    [Fact]
    public void Nvidia_smi_fields_it_cannot_read_are_empty_not_zero()
    {
        // A laptop GPU: no power limit, no fan.
        var row = GpuSources.ParseNvidiaLine("0, 5, 40, 6.36, [N/A], 512, 4096, [N/A], 300, 405", new Dictionary<int, string>());
        Assert.Equal(6.36, row!.Value.Gpu.PowerW);
        Assert.Null(row.Value.Gpu.PowerLimitW);
        Assert.Null(row.Value.Gpu.FanPct);
        Assert.Equal("NVIDIA GPU", row.Value.Gpu.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.")]
    public void Nvidia_smi_noise_is_skipped(string line) => Assert.Null(GpuSources.ParseNvidiaLine(line, new Dictionary<int, string>()));

    [Fact]
    public void A_gpu_name_with_a_comma_is_kept_whole() =>
        Assert.Equal("NVIDIA RTX A2000, 12GB", GpuSources.ParseNvidiaNames("0, NVIDIA RTX A2000, 12GB\n")[0]);

    [Fact]
    public void The_ping_window_averages_answers_and_counts_unanswered_pings_as_loss()
    {
        var w = new PingWindow("1.1.1.1", capacity: 4);
        w.Add(10);
        w.Add(null);
        var s = w.Add(20);
        Assert.Equal(20, s.LastMs);
        Assert.Equal(15, s.AvgMs);
        Assert.Equal(33.3, s.LossPct);
        Assert.Equal(3, s.Samples);

        w.Add(30);
        s = w.Add(40); // the first 10 ms left the window
        Assert.Equal(4, s.Samples);
        Assert.Equal(30, s.AvgMs);
        Assert.Equal(25, s.LossPct);
    }

    [Fact]
    public void With_every_ping_lost_there_is_no_average()
    {
        var w = new PingWindow("1.1.1.1");
        w.Add(null);
        var s = w.Add(null);
        Assert.Null(s.LastMs);
        Assert.Null(s.AvgMs);
        Assert.Equal(100, s.LossPct);
    }

    [Fact]
    public void The_cpu_package_sensor_wins_over_the_cores_and_the_board_takes_its_hottest()
    {
        var snap = new TemperatureSnapshot([
            new SensorReading("Core #1", "Cpu", 80),
            new SensorReading("CPU Package", "Cpu", 66.66),
            new SensorReading("Temperature #1", "Board", 41),
            new SensorReading("Temperature #2", "Board", 47),
            new SensorReading("GPU Core", "Gpu", 70),
        ]);
        Assert.Equal(((double?)66.7, (double?)47.0), PanelTemperatures.From(snap));
    }

    [Fact]
    public void Without_a_package_sensor_the_hottest_cpu_sensor_is_used_and_no_sensors_mean_none()
    {
        Assert.Equal(((double?)72.0, (double?)null), PanelTemperatures.From(new TemperatureSnapshot([new SensorReading("Core #1", "Cpu", 70), new SensorReading("Core #2", "Cpu", 72)])));
        Assert.Equal(((double?)null, (double?)null), PanelTemperatures.From(new TemperatureSnapshot([])));
        Assert.Equal(((double?)null, (double?)null), PanelTemperatures.From(null));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Realtek RTL8852BE WiFi 6 802.11ax PCIe Adapter", true)]
    [InlineData(NetworkInterfaceType.Ethernet, "이더넷", "Realtek PCIe GbE Family Controller", true)]
    [InlineData((NetworkInterfaceType)53, "Tailscale", "Tailscale Tunnel", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter #2", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "이더넷 3", "TAP-Win32 Adapter V9", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "Bluetooth 네트워크 연결", "Bluetooth Device (Personal Area Network)", false)]
    [InlineData(NetworkInterfaceType.Wireless80211, "로컬 영역 연결* 9", "Microsoft Wi-Fi Direct Virtual Adapter", false)]
    [InlineData(NetworkInterfaceType.Loopback, "Loopback Pseudo-Interface 1", "Software Loopback Interface 1", false)]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo Tunneling Pseudo-Interface", "Microsoft Teredo Tunneling Adapter", false)]
    public void Only_physical_adapters_count_toward_network_totals(NetworkInterfaceType type, string name, string description, bool counts) =>
        Assert.Equal(counts, WindowsResourceSampler.CountsTowardTotals(type, name, description));

    [Fact]
    public void Windows_hardware_flag_leaves_out_virtual_adapters_the_name_list_does_not_know()
    {
        // A new VPN whose name matches nothing in the list: Windows marks it as software.
        Assert.False(WindowsResourceSampler.CountsTowardTotals(NetworkInterfaceType.Ethernet, "이더넷 4", "Acme SecureLink Adapter", hardware: false));
        Assert.True(WindowsResourceSampler.CountsTowardTotals(NetworkInterfaceType.Ethernet, "이더넷", "Realtek PCIe GbE Family Controller", hardware: true));
        // Some virtual drivers register as hardware: the name rules still apply to them.
        Assert.False(WindowsResourceSampler.CountsTowardTotals(NetworkInterfaceType.Ethernet, "이더넷 3", "TAP-Windows Adapter V9", hardware: true));
        // Unknown (WMI not read yet): names alone decide, as before.
        Assert.True(WindowsResourceSampler.CountsTowardTotals(NetworkInterfaceType.Ethernet, "이더넷", "Realtek PCIe GbE Family Controller", hardware: null));
    }

    [Fact]
    public void The_hub_runs_the_hardware_sampler_only_while_a_phone_watches_and_puts_its_readings_in_the_snapshot()
    {
        var hardware = new FakeHardwareSampler();
        using var hub = new LiveSnapshotHub(new FakeSampler(), Options.Create(new MobileOptions()), TimeProvider.System,
            NullLogger<LiveSnapshotHub>.Instance, hardware);
        Assert.False(hardware.Started);
        Assert.Null(hub.Build().Hardware);

        var sub = hub.Subscribe();
        Assert.True(hardware.Started);
        Assert.Equal("Test CPU", hub.Build().Hardware?.CpuName);

        sub.Dispose();
        Assert.False(hardware.Started);
        Assert.Equal(1, hardware.StartCount);
    }

    [Fact]
    public void A_hub_without_a_hardware_sampler_sends_no_hardware()
    {
        using var hub = new LiveSnapshotHub(new FakeSampler(), Options.Create(new MobileOptions()), TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        using var sub = hub.Subscribe();
        Assert.Null(hub.Build().Hardware);
    }

    [Fact]
    public void An_empty_ping_host_turns_pinging_off()
    {
        var sampler = new WindowsHardwareSampler(Options.Create(new MobileOptions { PingHost = " " }), NullLogger<WindowsHardwareSampler>.Instance);
        Assert.Null(sampler.Current); // not started
        sampler.Start();
        try
        {
            Thread.Sleep(300);
            Assert.Null(sampler.Current!.Ping);
        }
        finally { sampler.Stop(); }
        Assert.Null(sampler.Current);
    }

    private sealed class FakeHardwareSampler : IHardwareSampler
    {
        public bool Started { get; private set; }
        public int StartCount { get; private set; }
        public void Start() { Started = true; StartCount++; }
        public void Stop() => Started = false;
        public HardwareInfo? Current => Started ? new HardwareInfo("Test CPU", null, null, [], [], null) : null;
    }
}
