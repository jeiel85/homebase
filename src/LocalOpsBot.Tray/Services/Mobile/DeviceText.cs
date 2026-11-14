using System.Globalization;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>One-line summaries of device state for the dashboard and the Phone tab (DECISIONS M-27, M-29).</summary>
internal static class DeviceText
{
    public static string Status(DeviceStatus? s, string kind)
    {
        if (s is null) return L.DevNoReport;
        var parts = new List<string>();
        if (kind == DeviceKind.Phone)
        {
            if (s.Battery is { } b) parts.Add(L.DevBattery(b, s.Charging == true));
            if (!string.IsNullOrEmpty(s.Network)) parts.Add(s.Network!);
        }
        else
        {
            if (s.CpuPct is { } cpu) parts.Add($"CPU {cpu.ToString("0", CultureInfo.InvariantCulture)}%");
            if (s.MemUsedBytes is { } used && s.MemTotalBytes is { } total && total > 0)
                parts.Add($"RAM {Gb(used)}/{Gb(total)} GB");
            if (s.UptimeSec is { } up) parts.Add(L.DevUptime(TimeSpan.FromSeconds(up)));
        }
        if (!string.IsNullOrEmpty(s.AppVersion)) parts.Add("v" + s.AppVersion);
        return parts.Count == 0 ? L.DevNoReport : string.Join(" · ", parts);
    }

    public static string Seen(DateTimeOffset? at, bool online)
    {
        if (online) return L.DevOnline;
        if (at is null) return L.DevOffline;
        return L.DevSeen(Ago(DateTimeOffset.UtcNow - at.Value));
    }

    public static string Entry(DeviceEntry d, string? via = null) =>
        $"{Seen(d.LastSeenAt, d.Online)} · {Status(d.Status, d.Kind)}" + (via is null ? "" : $" · {L.DevVia(via)}");

    public static string Hub(LinkedHubView h)
    {
        var state = h.Online ? L.DevOnline
            : h.Error == "rejected" ? L.DevHubRejected
            : h.LastContactAt is { } at ? L.DevSeen(Ago(DateTimeOffset.UtcNow - at)) : L.DevHubConnecting;
        var others = h.Devices.Count(d => d.Kind == DeviceKind.Phone);
        return $"{state} · {Status(h.Status, DeviceKind.Pc)}" + (others > 0 ? $" · {L.DevHubPhones(others)}" : "");
    }

    private static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? L.DevJustNow
        : t.TotalHours < 1 ? L.DevMinutes((int)t.TotalMinutes)
        : t.TotalDays < 1 ? L.DevHours((int)t.TotalHours)
        : L.DevDays((int)t.TotalDays);
}
