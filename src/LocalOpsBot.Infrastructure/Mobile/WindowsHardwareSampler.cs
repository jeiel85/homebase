using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Infrastructure.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Infrastructure.Mobile;

/// <summary>
/// The live view's hardware panel (DECISIONS M-34). Between <see cref="Start"/> and <see cref="Stop"/> three
/// loops run: GPUs (graphics kernel + counters), drives and temperatures every 2 s; a ping every 2 s; and a
/// streaming <c>nvidia-smi</c> where one is installed. Nothing runs while no phone is watching (M-5).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHardwareSampler : IHardwareSampler, IDisposable
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NvidiaStaleAfter = TimeSpan.FromSeconds(5);
    private const int TemperatureEvery = 3;   // × Period
    private const int DiskEvery = 8;          // × Period
    private const long MinDiskBytes = 2L * 1024 * 1024 * 1024;

    private readonly string? _pingHost;
    private readonly bool _nvidiaSmi;
    private readonly ITemperatureCollector? _temperature;
    private readonly ILogger<WindowsHardwareSampler> _logger;
    private readonly string? _cpuName = ReadCpuName();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    private volatile bool _running;
    private volatile IReadOnlyList<GpuReading>? _osGpus;
    private volatile IReadOnlyList<GpuReading>? _nvidia;
    private long _nvidiaAt;
    private volatile IReadOnlyList<DiskUsage> _disks = [];
    private volatile PingStats? _ping;
    private volatile Temps? _temps;
    /// <summary>nvidia-smi has failed and that was logged; kept across watch sessions so it is logged once.</summary>
    private volatile bool _nvidiaFailing;

    private sealed record Temps(double? Cpu, double? Board);

    public WindowsHardwareSampler(IOptions<MobileOptions> options, ILogger<WindowsHardwareSampler> logger, ITemperatureCollector? temperature = null)
    {
        var host = options.Value.PingHost?.Trim();
        _pingHost = string.IsNullOrEmpty(host) ? null : host;
        _nvidiaSmi = options.Value.NvidiaSmi;
        _temperature = temperature;
        _logger = logger;
    }

    public HardwareInfo? Current
    {
        get
        {
            if (!_running) return null;
            var nvidia = Stopwatch.GetElapsedTime(Interlocked.Read(ref _nvidiaAt)) <= NvidiaStaleAfter ? _nvidia : null;
            var temps = _temps;
            return new HardwareInfo(_cpuName, temps?.Cpu, temps?.Board,
                GpuSources.Compose(nvidia, _osGpus), _disks, _ping);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            _osGpus = null;
            _nvidia = null;
            _disks = [];
            _ping = null;
            _temps = null;
            Interlocked.Exchange(ref _nvidiaAt, 0);
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(() => DeviceLoopAsync(cts.Token));
            if (_nvidiaSmi) _ = Task.Run(() => NvidiaLoopAsync(cts.Token)); // mobile:nvidiaSmi
            if (_pingHost is not null) _ = Task.Run(() => PingLoopAsync(_pingHost, cts.Token));
            _running = true;
        }
    }

    /// <summary>
    /// Cancels the loops without waiting for them, so a caller holding a lock is not held up; a loop that
    /// is mid-reading stops at its next step, and the next <see cref="Start"/> begins from empty readings.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _cts?.Cancel(); // not disposed: a loop may still be reading its token
            _cts = null;
        }
    }

    public void Dispose() => Stop();

    // GPUs (every tick), temperatures and drives (every few ticks).
    private async Task DeviceLoopAsync(CancellationToken ct)
    {
        WindowsGpuReader? gpu = null;
        try { gpu = new WindowsGpuReader(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogInformation("GPU readings are unavailable on this Windows ({Message}).", ex.Message);
        }
        using var _ = gpu;
        var gpuFailed = false;
        try
        {
            for (var tick = 0; !ct.IsCancellationRequested; tick++)
            {
                if (gpu is not null)
                {
                    try
                    {
                        var gpus = gpu.Sample();
                        if (ct.IsCancellationRequested) break; // a stopped loop leaves the next run's readings alone
                        _osGpus = gpus;
                        gpuFailed = false;
                    }
                    catch (Exception ex)
                    {
                        if (!gpuFailed) _logger.LogWarning(ex, "Reading the GPUs failed; they are left out until it works again.");
                        gpuFailed = true;
                        _osGpus = null;
                    }
                }
                if (tick % TemperatureEvery == 0 && _temperature is not null)
                {
                    var result = await _temperature.CollectAsync(ct).ConfigureAwait(false);
                    var (cpu, board) = PanelTemperatures.From(result.Success ? result.Snapshot : null);
                    if (ct.IsCancellationRequested) break;
                    _temps = new Temps(cpu, board);
                }
                if (tick % DiskEvery == 0) _disks = ReadDisks();
                await Task.Delay(Period, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PingLoopAsync(string host, CancellationToken ct)
    {
        var window = new PingWindow(host);
        using var ping = new Ping();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                double? ms = null;
                try
                {
                    var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(1500), cancellationToken: ct).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success) ms = reply.RoundtripTime;
                }
                catch (PingException) { /* no route, unknown host: a lost ping */ }
                if (ct.IsCancellationRequested) break;
                _ping = window.Add(ms);
                await Task.Delay(Period, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Keeps one <c>nvidia-smi -lms 1000</c> running and reads one line per GPU per second. A PC without
    /// nvidia-smi ends the loop at once; a failing one is retried every 30 s. A failure (a driver update
    /// can take minutes) is logged once as a warning, and its end once as information.
    /// </summary>
    private async Task NvidiaLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Exception? error = null;
                try
                {
                    var names = GpuSources.ParseNvidiaNames(await RunOnceAsync("--query-gpu=index,name --format=csv,noheader", ct).ConfigureAwait(false));
                    if (names.Count == 0) return; // the tool is there but sees no NVIDIA card
                    var psi = Psi($"--query-gpu={GpuSources.NvidiaQuery} --format=csv,noheader,nounits -lms 1000");
                    using var p = Process.Start(psi) ?? throw new InvalidOperationException("nvidia-smi did not start");
                    using var job = KillOnCloseJob.TryAssign(p, _logger, "nvidia-smi");
                    using var reg = ct.Register(() => { try { p.Kill(); } catch (InvalidOperationException) { } });
                    var cards = new SortedDictionary<int, GpuReading>();
                    while (await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    {
                        if (GpuSources.ParseNvidiaLine(line, names) is not { } row || ct.IsCancellationRequested) continue;
                        cards[row.Index] = row.Gpu;
                        _nvidia = cards.Values.ToList();
                        Interlocked.Exchange(ref _nvidiaAt, Stopwatch.GetTimestamp());
                        if (_nvidiaFailing)
                        {
                            _nvidiaFailing = false;
                            _logger.LogInformation("nvidia-smi works again; NVIDIA power and clock readings are back.");
                        }
                    }
                }
                catch (Win32Exception)
                {
                    return; // not installed: no NVIDIA driver on this PC
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    error = ex;
                }
                if (ct.IsCancellationRequested) break; // killed by Stop: not a failure to retry
                if (!_nvidiaFailing)
                {
                    _nvidiaFailing = true;
                    _logger.LogWarning("nvidia-smi stopped ({Error}); NVIDIA power and clock readings are left out, retrying every 30 s.",
                        error?.Message ?? "it exited");
                }
                else
                {
                    _logger.LogDebug(error, "nvidia-smi still not running; trying again in 30 s.");
                }
                _nvidia = null;
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<string> RunOnceAsync(string args, CancellationToken ct)
    {
        using var p = Process.Start(Psi(args)) ?? throw new InvalidOperationException("nvidia-smi did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var text = await p.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return p.ExitCode == 0 ? text : "";
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static ProcessStartInfo Psi(string args) => new("nvidia-smi", args)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Environment.SystemDirectory,
    };

    /// <summary>Fixed drives over 2 GB (no recovery or EFI partitions), by drive letter.</summary>
    private static IReadOnlyList<DiskUsage> ReadDisks()
    {
        var list = new List<DiskUsage>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize < MinDiskBytes) continue;
                list.Add(new DiskUsage(d.Name.TrimEnd('\\'), d.TotalSize, d.TotalSize - d.TotalFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a drive going away */ }
            if (list.Count == 8) break;
        }
        return list;
    }

    private static string? ReadCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = (key?.GetValue("ProcessorNameString") as string)?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
