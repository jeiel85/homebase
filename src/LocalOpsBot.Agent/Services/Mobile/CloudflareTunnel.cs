using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>
/// Runs the Cloudflare Tunnel connector (<c>cloudflared</c>) as a child of the Agent while
/// <c>mobile:cloudflare:enabled</c> is set (DECISIONS M-17). The connector dials out to Cloudflare
/// and forwards to the mobile API on 127.0.0.1, so no inbound port is opened.
/// <list type="bullet">
/// <item>Token configured → that named tunnel (stable hostname from config). The token is passed
/// in the <c>TUNNEL_TOKEN</c> environment variable, never on the command line.</item>
/// <item>No token → an account-less Quick Tunnel; its random <c>*.trycloudflare.com</c> address is
/// read from the connector's log and changes on every start.</item>
/// </list>
/// The process sits in a kill-on-close Job Object so it never outlives the Agent. Failures are
/// logged and retried with backoff; nothing here can stop the host (and Telegram monitoring).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class CloudflareTunnel : BackgroundService
{
    private static readonly TimeSpan[] Backoff =
        { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5) };

    /// <summary>A connector that stays up this long resets the backoff.</summary>
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan NotInstalledRecheck = TimeSpan.FromMinutes(1);

    [GeneratedRegex(@"https://([a-z0-9-]+\.trycloudflare\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex QuickTunnelUrl();

    [GeneratedRegex(@"Registered tunnel connection", RegexOptions.IgnoreCase)]
    private static partial Regex Registered();

    private readonly MobileOptions _options;
    private readonly ILogger<CloudflareTunnel> _logger;
    private readonly object _gate = new();
    private CloudflareStatus? _status;

    public CloudflareTunnel(IOptions<MobileOptions> options, ILogger<CloudflareTunnel> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (_options.Cloudflare.Enabled)
            _status = new CloudflareStatus("starting", Mode, NamedHostname, null);
    }

    private bool IsNamed => _options.Cloudflare.ResolveToken() is not null;

    private string Mode => IsNamed ? "named" : "quick";

    private string? NamedHostname => IsNamed ? CleanHostname(_options.Cloudflare.Hostname) : null;

    /// <summary>Connector state for the Tray, or <c>null</c> when the tunnel is off.</summary>
    public CloudflareStatus? Status()
    {
        lock (_gate) return _status;
    }

    /// <summary>The phone-facing endpoint while the tunnel is up.</summary>
    public MobileEndpoint? Endpoint()
    {
        lock (_gate)
        {
            return _status is { State: "running", Hostname: { Length: > 0 } host }
                ? new MobileEndpoint(MobileTransport.Cloudflare, host, 443, Tls: true)
                : null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Cloudflare.Enabled) return;
        await Task.Yield(); // don't hold up host start-up

        if (IsNamed && NamedHostname is null)
        {
            SetStatus("error", null, "mobile:cloudflare:hostname is required with a tunnel token.");
            _logger.LogWarning("Cloudflare Tunnel: a token is set but mobile:cloudflare:hostname is empty; the tunnel is not started.");
            return;
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var exe = LocateCloudflared(_options.Cloudflare.CloudflaredPath);
            if (exe is null)
            {
                SetStatus("not_installed", null, "cloudflared.exe was not found (winget install Cloudflare.cloudflared).");
                await DelaySafe(NotInstalledRecheck, stoppingToken);
                continue;
            }

            var started = DateTimeOffset.UtcNow;
            try
            {
                SetStatus("starting", NamedHostname, null);
                var exitCode = await RunOnceAsync(exe, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;
                SetStatus("error", null, $"cloudflared exited with code {exitCode}. {LastError}".Trim());
                _logger.LogWarning("Cloudflare Tunnel connector exited ({Code}); restarting.", exitCode);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                SetStatus("error", null, ex.Message);
                _logger.LogWarning(ex, "Cloudflare Tunnel connector failed; restarting.");
            }

            failures = DateTimeOffset.UtcNow - started >= HealthyRun ? 0 : failures + 1;
            await DelaySafe(Backoff[Math.Min(Math.Max(failures - 1, 0), Backoff.Length - 1)], stoppingToken);
        }
    }

    private string? LastError { get; set; }

    private async Task<int> RunOnceAsync(string exe, CancellationToken ct)
    {
        LastError = null;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("tunnel");
        psi.ArgumentList.Add("--no-autoupdate");
        var token = _options.Cloudflare.ResolveToken();
        if (token is not null)
        {
            psi.ArgumentList.Add("run");
            psi.Environment["TUNNEL_TOKEN"] = token;
        }
        else
        {
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add($"http://127.0.0.1:{_options.Port}");
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLogLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLogLine(e.Data);
        if (!process.Start()) throw new InvalidOperationException("cloudflared did not start.");
        using var job = KillOnCloseJob.TryAssign(process, _logger);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _logger.LogInformation("Cloudflare Tunnel connector started ({Mode}, pid {Pid}).", Mode, process.Id);

        try
        {
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private void OnLogLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        _logger.LogDebug("cloudflared: {Line}", line);

        if (!IsNamed && ParseQuickTunnelHost(line) is { } host)
        {
            SetStatus("running", host, null);
            _logger.LogInformation("Cloudflare Quick Tunnel is up at https://{Host} (this address changes on every start).", host);
        }
        else if (IsNamed && Registered().IsMatch(line))
        {
            if (Status()?.State != "running")
                _logger.LogInformation("Cloudflare Tunnel connected; phones can use https://{Host}.", NamedHostname);
            SetStatus("running", NamedHostname, null);
        }
        else if (line.Contains(" ERR ", StringComparison.Ordinal))
        {
            LastError = line.Length > 300 ? line[..300] : line;
        }
    }

    private void SetStatus(string state, string? hostname, string? detail)
    {
        lock (_gate) _status = new CloudflareStatus(state, Mode, hostname, detail);
    }

    /// <summary>The <c>*.trycloudflare.com</c> host announced in a Quick Tunnel log line, if any.</summary>
    public static string? ParseQuickTunnelHost(string line) =>
        QuickTunnelUrl().Match(line) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    /// <summary>Lowercase host with any scheme, path or port stripped; <c>null</c> when empty.</summary>
    public static string? CleanHostname(string? raw)
    {
        var h = raw?.Trim();
        if (string.IsNullOrEmpty(h)) return null;
        if (h.Contains("://", StringComparison.Ordinal)) h = h[(h.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var cut = h.IndexOfAny(new[] { '/', ':', '?', '#' });
        if (cut >= 0) h = h[..cut];
        h = h.TrimEnd('.').ToLowerInvariant();
        return h.Length == 0 ? null : h;
    }

    /// <summary>Configured path, then the winget/MSI install folders, then PATH.</summary>
    public static string? LocateCloudflared(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var candidate = Path.Combine(Environment.GetFolderPath(folder), "cloudflared", "cloudflared.exe");
            if (File.Exists(candidate)) return candidate;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "cloudflared.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    /// <summary>Job Object with KILL_ON_JOB_CLOSE: the connector dies with the Agent, even on a crash.</summary>
    private sealed class KillOnCloseJob : IDisposable
    {
        private readonly IntPtr _handle;

        private KillOnCloseJob(IntPtr handle) => _handle = handle;

        public static KillOnCloseJob? TryAssign(Process process, ILogger logger)
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return null;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)size) ||
                !AssignProcessToJobObject(job, process.Handle))
            {
                logger.LogDebug("Could not place cloudflared in a job object (error {Error}).", Marshal.GetLastWin32Error());
                CloseHandle(job);
                return null;
            }
            return new KillOnCloseJob(job);
        }

        public void Dispose() => CloseHandle(_handle);

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
