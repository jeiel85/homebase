using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Core.Commands;

public sealed class LlmCommandHandler : ICommandHandler
{
    private readonly ITcpPortMonitor _tcpMonitor;

    public string CommandName => "llm";
    public string Description => "Local LLM server status (Ollama, LM Studio)";

    // Well-known local LLM server endpoints, checked by TCP reachability.
    private static readonly TcpPortConfig[] KnownServers =
    {
        new("Ollama", "127.0.0.1", 11434),
        new("LM Studio", "127.0.0.1", 1234),
    };

    public LlmCommandHandler(ITcpPortMonitor tcpMonitor) => _tcpMonitor = tcpMonitor;

    public async Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var lines = new List<string> { "<b>\U0001f9e0 Local LLM Status</b>\n" };

        foreach (var server in KnownServers)
        {
            var result = await _tcpMonitor.CheckAsync(server, ct);
            var icon = result.Open ? "✅" : "❌";
            var status = result.Open ? $"running ({result.ResponseTimeMs}ms)" : "not running";
            lines.Add($"{icon} <b>{server.Name}</b> — {status}");
            lines.Add($"  <code>{server.Host}:{server.Port}</code>");
        }

        return new CommandResult(true, string.Join("\n", lines));
    }
}
