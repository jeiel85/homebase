using Microsoft.Extensions.Configuration;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Reads the Agent's monitor configuration (process/service watches, dev HTTP/TCP monitors) into
/// simple one-per-line string lists for the dashboard editor. The essential field of each entry is
/// surfaced; the richer options (severity, timeouts, expected codes) keep their defaults.
/// </summary>
internal static class MonitorsConfig
{
    internal sealed record Snapshot(
        IReadOnlyList<string> Processes,
        IReadOnlyList<string> Services,
        IReadOnlyList<string> HttpEndpoints,
        IReadOnlyList<string> TcpPorts);

    public static Snapshot Read()
    {
        var cfg = TrayConfig.Load();
        return new Snapshot(
            ReadProcesses(cfg),
            ReadServices(cfg),
            ReadHttp(cfg),
            ReadTcp(cfg));
    }

    private static List<string> ReadProcesses(IConfiguration cfg)
    {
        var list = new List<string>();
        foreach (var item in cfg.GetSection("processWatches").GetChildren())
        {
            var pn = item.GetSection("processNames").GetChildren()
                         .Select(c => c.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var value = pn ?? item["name"];
            if (!string.IsNullOrWhiteSpace(value)) list.Add(value);
        }
        return list;
    }

    private static List<string> ReadServices(IConfiguration cfg)
    {
        var list = new List<string>();
        foreach (var item in cfg.GetSection("serviceWatches").GetChildren())
        {
            var value = item["serviceName"] ?? item["name"];
            if (!string.IsNullOrWhiteSpace(value)) list.Add(value);
        }
        return list;
    }

    private static List<string> ReadHttp(IConfiguration cfg)
    {
        var list = new List<string>();
        foreach (var item in cfg.GetSection("developerMonitors:httpEndpoints").GetChildren())
        {
            var url = item["url"];
            if (!string.IsNullOrWhiteSpace(url)) list.Add(url);
        }
        return list;
    }

    private static List<string> ReadTcp(IConfiguration cfg)
    {
        var list = new List<string>();
        foreach (var item in cfg.GetSection("developerMonitors:tcpPorts").GetChildren())
        {
            var host = item["host"];
            var port = item["port"];
            if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(port))
                list.Add($"{host}:{port}");
        }
        return list;
    }
}
