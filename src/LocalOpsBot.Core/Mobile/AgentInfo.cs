namespace LocalOpsBot.Core.Mobile;

/// <summary>Values of <see cref="AgentInfo.Status"/> (DECISIONS M-44).</summary>
public static class AgentStatus
{
    /// <summary>Working on a turn: thinking, running a tool, writing.</summary>
    public const string Busy = "busy";

    /// <summary>Stopped until the user answers: a permission prompt or a question.</summary>
    public const string Waiting = "waiting";

    /// <summary>Open, with no turn in progress.</summary>
    public const string Idle = "idle";
}

/// <summary>
/// What a work-tab agent entry knows about its session beyond title and folder (DECISIONS M-44).
/// Every field is optional: each comes from a file the AI tool writes for itself, and a field the
/// tool did not write (or a newer format this PC cannot read) stays null instead of failing the entry.
/// No prompt, reply or tool argument is carried; <see cref="LastTool"/> is a tool name only.
/// </summary>
/// <param name="Status"><see cref="AgentStatus"/>; null when this PC cannot tell.</param>
/// <param name="WaitingFor">Claude Code's own short reason while <see cref="AgentStatus.Waiting"/>.</param>
/// <param name="StatusSince">When the session entered <see cref="Status"/>.</param>
/// <param name="ContextTokens">Tokens the last request sent: how full the context is.</param>
/// <param name="ContextWindow">The model's context size, when the tool records it (Codex).</param>
/// <param name="TotalTokens">Tokens used by the whole session, when the tool records it (Codex).</param>
/// <param name="Mode">Claude Code's permission mode (<c>plan</c>, <c>acceptEdits</c>, …) other than the default.</param>
/// <param name="PrNumber">The pull request the session opened or is working on.</param>
/// <param name="RemoteUrl">The session on claude.ai (Remote Control), where the phone can type to it.</param>
/// <param name="SessionId">The tool's own session id.</param>
public sealed record AgentInfo(
    string? Status = null,
    string? WaitingFor = null,
    DateTimeOffset? StatusSince = null,
    string? Model = null,
    string? Effort = null,
    long? ContextTokens = null,
    long? ContextWindow = null,
    long? TotalTokens = null,
    string? LastTool = null,
    string? Mode = null,
    int? PrNumber = null,
    string? RemoteUrl = null,
    string? SessionId = null);
