using System.Text.Json.Nodes;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// Writes the dashboard's monitor lists to the Agent config via <see cref="ElevatedConfig"/>. Each
/// entry is written as a minimal record (name + the essential field); the config binder fills the
/// remaining fields (severity, method, timeout, expected codes) from their defaults.
/// </summary>
internal static class MonitorsConfigurator
{
    public static async Task<bool> SaveAsync(
        IEnumerable<string> processes, IEnumerable<string> services,
        IEnumerable<string> httpUrls, IEnumerable<string> tcpEntries)
    {
        var root = await ElevatedConfig.ReadAsync();

        root["processWatches"] = BuildProcesses(processes);
        root["serviceWatches"] = BuildServices(services);

        if (root["developerMonitors"] is not JsonObject dev)
        {
            dev = new JsonObject();
            root["developerMonitors"] = dev;
        }
        dev["httpEndpoints"] = BuildHttp(httpUrls);
        dev["tcpPorts"] = BuildTcp(tcpEntries);

        return await ElevatedConfig.WriteAsync(root);
    }

    private static JsonArray BuildProcesses(IEnumerable<string> names)
    {
        var arr = new JsonArray();
        foreach (var n in Clean(names))
            arr.Add(new JsonObject { ["name"] = n, ["processNames"] = new JsonArray(n) });
        return arr;
    }

    private static JsonArray BuildServices(IEnumerable<string> names)
    {
        var arr = new JsonArray();
        foreach (var n in Clean(names))
            arr.Add(new JsonObject { ["name"] = n, ["serviceName"] = n });
        return arr;
    }

    private static JsonArray BuildHttp(IEnumerable<string> urls)
    {
        var arr = new JsonArray();
        foreach (var u in Clean(urls))
            arr.Add(new JsonObject { ["name"] = u, ["url"] = u });
        return arr;
    }

    private static JsonArray BuildTcp(IEnumerable<string> entries)
    {
        var arr = new JsonArray();
        foreach (var e in Clean(entries))
        {
            // "host:port" — split on the last colon so IPv6-ish hosts aren't mangled here.
            var idx = e.LastIndexOf(':');
            if (idx <= 0 || idx == e.Length - 1) continue;
            var host = e[..idx].Trim();
            if (host.Length == 0 || !int.TryParse(e[(idx + 1)..].Trim(), out var port)) continue;
            arr.Add(new JsonObject { ["name"] = e, ["host"] = host, ["port"] = port });
        }
        return arr;
    }

    private static IEnumerable<string> Clean(IEnumerable<string> items) =>
        items.Select(s => s?.Trim() ?? string.Empty)
             .Where(s => s.Length > 0)
             .Distinct(StringComparer.OrdinalIgnoreCase);
}
