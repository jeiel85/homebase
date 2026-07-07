namespace LocalOpsBot.Core.Monitoring;

public sealed record HttpEndpointResult(
    string Name, string Url, bool Success,
    int? StatusCode, long? ResponseTimeMs, string? Error);

public sealed record TcpPortResult(
    string Name, string Host, int Port, bool Open, long? ResponseTimeMs, string? Error);

public interface IHttpEndpointMonitor
{
    Task<HttpEndpointResult> CheckAsync(HttpEndpointConfig config, CancellationToken ct);
}

public interface ITcpPortMonitor
{
    Task<TcpPortResult> CheckAsync(TcpPortConfig config, CancellationToken ct);
}
