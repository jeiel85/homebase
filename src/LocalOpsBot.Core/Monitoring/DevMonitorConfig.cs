namespace LocalOpsBot.Core.Monitoring;

public sealed record HttpEndpointConfig(
    string Name,
    string Url,
    string Method = "GET",
    int TimeoutSeconds = 5,
    int[]? ExpectedStatusCodes = null);

public sealed record TcpPortConfig(
    string Name,
    string Host,
    int Port);

public sealed record DevMonitorOptions(
    IReadOnlyList<HttpEndpointConfig> HttpEndpoints,
    IReadOnlyList<TcpPortConfig> TcpPorts);
