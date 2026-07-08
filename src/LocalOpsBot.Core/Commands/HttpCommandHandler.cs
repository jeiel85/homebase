using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Commands;

public sealed class HttpCommandHandler : ICommandHandler
{
    private readonly IHttpEndpointMonitor _httpMonitor;
    private readonly IReadOnlyList<HttpEndpointConfig> _endpoints;

    public string CommandName => "http";
    public string Description => "Monitored HTTP endpoint status";

    public HttpCommandHandler(
        IHttpEndpointMonitor httpMonitor,
        IEnumerable<HttpEndpointConfig> endpoints)
    {
        _httpMonitor = httpMonitor;
        _endpoints = endpoints.ToList();
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        if (_endpoints.Count == 0)
            return new CommandResult(false, "No HTTP endpoints configured.\nAdd `developerMonitors.httpEndpoints` to config.");

        var lines = new List<string> { "<b>\U0001f310 HTTP Endpoint Status</b>\n" };

        foreach (var ep in _endpoints)
        {
            var result = await _httpMonitor.CheckAsync(ep, ct);
            var icon = result.Success ? "✅" : "❌";
            var ms = result.ResponseTimeMs.HasValue ? $" ({result.ResponseTimeMs}ms)" : "";
            lines.Add($"{icon} <b>{HtmlEscape(result.Name)}</b>{ms}");
            lines.Add($"  {HtmlEscape(result.Url)}");
            lines.Add($"  {(result.Success ? "OK" : HtmlEscape(result.Error ?? "down"))}");
        }

        return new CommandResult(true, string.Join("\n", lines));
    }

    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
