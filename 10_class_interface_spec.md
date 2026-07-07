# 10. Class and Interface Specification

## 1. Namespace 구조

```text
LocalOpsBot.Core
LocalOpsBot.Core.Commands
LocalOpsBot.Core.Alerts
LocalOpsBot.Core.Monitoring
LocalOpsBot.Core.Notifications
LocalOpsBot.Infrastructure.Telegram
LocalOpsBot.Infrastructure.Windows
LocalOpsBot.Infrastructure.Security
LocalOpsBot.Data.Sqlite
LocalOpsBot.Agent.Services
LocalOpsBot.Tray.Services
```

## 2. Options classes

```csharp
public sealed class AppOptions
{
    public TelegramOptions Telegram { get; init; } = new();
    public AgentOptions Agent { get; init; } = new();
    public AlertingOptions Alerting { get; init; } = new();
    public CollectorOptions Collectors { get; init; } = new();
    public NotificationForwardingOptions NotificationForwarding { get; init; } = new();
}
```

```csharp
public sealed class TelegramOptions
{
    public string BotToken { get; init; } = string.Empty;
    public IReadOnlyList<long> AllowedChatIds { get; init; } = Array.Empty<long>();
    public int PollingTimeoutSeconds { get; init; } = 30;
    public int PollingErrorBackoffSeconds { get; init; } = 10;
    public string ParseMode { get; init; } = "HTML";
    public bool DisableWebPagePreview { get; init; } = true;
    public bool RespondToUnauthorized { get; init; } = false;
}
```

## 3. Telegram models

```csharp
public sealed record TelegramUpdate(
    long UpdateId,
    TelegramMessage? Message);
```

```csharp
public sealed record TelegramMessage(
    long MessageId,
    TelegramChat Chat,
    TelegramUser? From,
    string? Text,
    DateTimeOffset Date);
```

```csharp
public sealed record TelegramChat(long Id, string Type, string? Title, string? Username);
```

```csharp
public sealed record TelegramUser(long Id, bool IsBot, string? Username, string? FirstName);
```

## 4. Telegram client

```csharp
public interface ITelegramClient
{
    Task SendMessageAsync(
        long chatId,
        string text,
        TelegramSendOptions? options,
        CancellationToken ct);

    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long? offset,
        int timeoutSeconds,
        CancellationToken ct);
}
```

```csharp
public sealed record TelegramSendOptions(
    string? ParseMode = "HTML",
    bool DisableWebPagePreview = true,
    bool DisableNotification = false);
```

## 5. Command model

```csharp
public sealed record BotCommand(
    string Name,
    IReadOnlyList<string> Args,
    long ChatId,
    long? UserId,
    string RawText,
    DateTimeOffset ReceivedAt);
```

```csharp
public sealed record CommandResult(
    bool Success,
    string ResponseText,
    bool SendResponse = true,
    string? Error = null);
```

```csharp
public interface ICommandHandler
{
    string CommandName { get; }
    string Description { get; }
    Task<CommandResult> HandleAsync(BotCommand command, CancellationToken ct);
}
```

```csharp
public interface ICommandRouter
{
    Task<CommandResult> RouteAsync(BotCommand command, CancellationToken ct);
}
```

## 6. Allowlist policy

```csharp
public interface IChatAuthorizationPolicy
{
    bool IsAllowed(long chatId, long? userId);
}
```

```csharp
public sealed class AllowedChatPolicy : IChatAuthorizationPolicy
{
    private readonly HashSet<long> _allowedChatIds;

    public bool IsAllowed(long chatId, long? userId)
        => _allowedChatIds.Contains(chatId);
}
```

## 7. Collector interfaces

```csharp
public sealed record CollectorResult<T>(
    bool Success,
    T? Snapshot,
    string? Error,
    DateTimeOffset CollectedAt)
{
    public static CollectorResult<T> Ok(T snapshot, DateTimeOffset at) => new(true, snapshot, null, at);
    public static CollectorResult<T> Fail(string error, DateTimeOffset at) => new(false, default, error, at);
}
```

```csharp
public interface ISystemMetricsCollector : ICollector<SystemMetricSnapshot> { }
public interface IDiskCollector : ICollector<IReadOnlyList<DiskSnapshot>> { }
public interface INetworkStatusChecker : ICollector<NetworkStatusSnapshot> { }
public interface IProcessCollector { Task<IReadOnlyList<ProcessWatchStatus>> CollectAsync(CancellationToken ct); }
public interface IWindowsServiceCollector { Task<IReadOnlyList<WindowsServiceWatchStatus>> CollectAsync(CancellationToken ct); }
```

## 8. Alert model

```csharp
public sealed record AlertEvent(
    string AlertId,
    string Kind,
    AlertSeverity Severity,
    string Title,
    string Body,
    string DedupKey,
    string Source,
    DateTimeOffset CreatedAt);
```

```csharp
public interface IAlertDispatcher
{
    Task DispatchAsync(AlertEvent alert, CancellationToken ct);
}
```

```csharp
public interface IAlertPolicy
{
    Task<AlertDecision> ShouldSendAsync(AlertEvent alert, CancellationToken ct);
}
```

```csharp
public sealed record AlertDecision(bool Send, string? DropReason);
```

## 9. Formatters

```csharp
public interface IMessageFormatter<in T>
{
    string Format(T value);
}
```

Implementations:

- `StatusMessageFormatter`
- `DiskMessageFormatter`
- `BootMessageFormatter`
- `ProcessWatchMessageFormatter`
- `EventLogMessageFormatter`
- `ToastNotificationMessageFormatter`

Formatter rules:

- Telegram HTML escape
- max message length guard
- null value를 `unknown`으로 표시
- 숫자는 사람이 읽기 좋게 변환

## 10. Notification interfaces

> **설계 보강 반영**: IPC 프로토콜 세부사항은 `16_design_supplement.md §2` 참조.

```csharp
// Agent 측: Named Pipe 서버 (수신)
public interface INotificationBridgeServer
{
    Task StartAsync(CancellationToken ct);
}
```

```csharp
// Tray 측: Named Pipe 클라이언트 (송신)
public interface INotificationBridgeClient
{
    Task SendAsync(ToastNotificationEvent notification, CancellationToken ct);
}
```

```csharp
// IPC 메시지 모델 (length-prefixed JSON)
public sealed record NotificationPipeMessage(
    int SchemaVersion,
    string Type,
    string EventId,
    string SourceApp,
    string? Title,
    string? Body,
    DateTimeOffset CreatedAt,
    NotificationSensitivity Sensitivity);
```

```csharp
public enum NotificationSensitivity { Normal, Sensitive, Blocked }
```

```csharp
public interface INotificationFilter
{
    NotificationFilterResult Evaluate(ToastNotificationEvent notification);
}
```

```csharp
public sealed record NotificationFilterResult(bool Allowed, string? DropReason);
```

## 11. Data repositories

```csharp
public interface IRuntimeStateRepository
{
    Task<T?> GetJsonAsync<T>(string key, CancellationToken ct);
    Task SetJsonAsync<T>(string key, T value, CancellationToken ct);
    Task<string?> GetStringAsync(string key, CancellationToken ct);
    Task SetStringAsync(string key, string value, CancellationToken ct);
}
```

```csharp
public interface IAlertRepository
{
    Task SaveAsync(AlertEvent alert, string status, string? error, CancellationToken ct);
    Task<bool> HasRecentDedupAsync(string dedupKey, TimeSpan window, CancellationToken ct);
    Task<IReadOnlyList<AlertLogEntry>> GetRecentAsync(int count, CancellationToken ct);
}
```

## 12. Service registration extensions

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLocalOpsCore(this IServiceCollection services, IConfiguration config);
    public static IServiceCollection AddLocalOpsTelegram(this IServiceCollection services, IConfiguration config);
    public static IServiceCollection AddLocalOpsData(this IServiceCollection services, IConfiguration config);
    public static IServiceCollection AddLocalOpsWindowsCollectors(this IServiceCollection services, IConfiguration config);
}
```

## 13. Command handlers list

```text
PingCommandHandler
HelpCommandHandler
StatusCommandHandler
UptimeCommandHandler
DiskCommandHandler
ProcessCommandHandler
ServicesCommandHandler
WatchCommandHandler
EventsCommandHandler
AlertsCommandHandler
MuteCommandHandler
UnmuteCommandHandler
PolicyCommandHandler
DiagnosticsCommandHandler
```

## 14. 예외 정책

Custom exceptions:

```csharp
public sealed class ConfigurationValidationException : Exception { }
public sealed class TelegramApiException : Exception { }
public sealed class CollectorException : Exception { }
```

단, collector 내부에서는 예외를 되도록 `CollectorResult.Fail`로 변환한다.
