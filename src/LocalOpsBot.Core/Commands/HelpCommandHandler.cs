using Microsoft.Extensions.DependencyInjection;

namespace LocalOpsBot.Core.Commands;

public sealed class HelpCommandHandler : ICommandHandler
{
    private readonly IServiceProvider _services;

    public string CommandName => "help";
    public string Description => "List available commands";

    // NOTE: resolve handlers lazily at call time. Injecting IEnumerable<ICommandHandler>
    // directly would create a DI cycle because HelpCommandHandler is itself an ICommandHandler.
    public HelpCommandHandler(IServiceProvider services)
    {
        _services = services;
    }

    public Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct)
    {
        var lines = new List<string>
        {
            "<b>\u2139\ufe0f LocalOps Bot Commands</b>\n"
        };

        foreach (var h in _services.GetServices<ICommandHandler>().OrderBy(h => h.CommandName))
        {
            lines.Add($"<b>/{h.CommandName}</b> — {HtmlEscape(h.Description)}");
        }

        lines.Add("");
        lines.Add("Tip: Use /mute 1h to silence alerts, /unmute to resume.");
        lines.Add("Source: https://github.com/anomalyco/localops_bot");

        return Task.FromResult(new CommandResult(true, string.Join("\n", lines)));
    }

    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
