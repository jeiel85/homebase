using LocalOpsBot.Protocol.Messaging;

namespace LocalOpsBot.Core.Commands;

public enum CommandPrincipalKind
{
    TelegramChat,
    PairedDevice,
    LocalUser
}

public enum CommandTrustLevel
{
    Untrusted,
    Allowed,
    Paired,
    LocalAdministrator
}

public enum CommandRiskLevel
{
    ReadOnly,
    UserSessionMutation,
    SystemMutation,
    Destructive
}

public sealed record CommandPrincipal(
    string PrincipalId,
    CommandPrincipalKind Kind,
    string? DeviceId,
    CommandTrustLevel TrustLevel);

public sealed record RemoteCommand(
    Guid RequestId,
    string Name,
    IReadOnlyList<string> Args,
    CommandPrincipal Principal,
    EndpointAddress ReplyTo,
    string RawInput,
    DateTimeOffset ReceivedAt,
    long? LegacyChatId = null,
    long? LegacyUserId = null);

public sealed record RemoteCommandResult(
    bool Success,
    string ResponseText,
    bool SendResponse = true,
    string? Error = null);
