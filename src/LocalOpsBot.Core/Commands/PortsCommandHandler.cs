using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Commands;

public sealed class PortsCommandHandler : ICommandHandler
{
    private readonly ITcpPortMonitor _tcpMonitor;
    private readonly IReadOnlyList<TcpPortConfig> _ports;

    public string CommandName => "ports";
    public string Description => "TCP port status";

    public PortsCommandHandler(
        ITcpPortMonitor tcpMonitor,
        IEnumerable<TcpPortConfig> ports)
    {
        _tcpMonitor = tcpMonitor;
        _ports = ports.ToList();
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        if (_ports.Count == 0)
            return new CommandResult(false, "No TCP ports configured.\nAdd `developerMonitors.tcpPorts` to config.");

        var lines = new List<string> { "<b>\ud83d\udd17 TCP Port Status</b>\n" };

        foreach (var p in _ports)
        {
            var result = await _tcpMonitor.CheckAsync(p, ct);
            var icon = result.Open ? "\u2705" : "\u274c";
            var ms = result.ResponseTimeMs.HasValue ? $" ({result.ResponseTimeMs}ms)" : "";
            lines.Add($"{icon} <b>{HtmlEscape(result.Name)}</b>{ms}");
            lines.Add($"  Host: {result.Host}:{result.Port}");
            lines.Add($"  Status: {(result.Open ? "Open" : result.Error)}");
            lines.Add("");
        }

        return new CommandResult(true, string.Join("\n", lines));
    }

    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
