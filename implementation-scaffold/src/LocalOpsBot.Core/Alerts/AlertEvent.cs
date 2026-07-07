namespace LocalOpsBot.Core.Alerts;

public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
    Recovery
}

public sealed record AlertEvent(
    string AlertId,
    string Kind,
    AlertSeverity Severity,
    string Title,
    string Body,
    string DedupKey,
    string Source,
    DateTimeOffset CreatedAt);
