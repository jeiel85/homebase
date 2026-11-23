using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>An HTTP answer from a hub: status and body, or <c>Status=0</c> when no path reached it.</summary>
public sealed record HubReply(int Status, string? Body, MobileEndpoint? Via, string? Error)
{
    public bool Ok => Status is >= 200 and < 300;

    public T? Read<T>() where T : class
    {
        if (Body is null) return null;
        try { return JsonSerializer.Deserialize<T>(Body, MobileProtocol.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The <c>error</c> code of a <c>{schema, error, message}</c> body.</summary>
    public string? ErrorCode => Read<ErrorResponse>()?.Error;
}

/// <summary>
/// Calls another PC's mobile API over its paths, in the order given (DECISIONS M-29), with the same
/// rules as the phone app: LAN is TLS whose certificate must match the pinned SHA-256, Tailscale is
/// plain HTTP (WireGuard encrypts), Cloudflare is public TLS. Loopback is used only when allowed
/// (tests): a remote PC's 127.0.0.1 would otherwise reach this PC's own Agent.
/// The path that answered last is tried first next time. Cloudflare's edge answering that the tunnel
/// behind it is gone (502, 504, 520–530 — a Quick Tunnel that restarted under a new name) is not the
/// PC's answer, so the next path is tried (M-59).
/// </summary>
public sealed class HubClient : IDisposable
{
    private readonly bool _allowLoopback;
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private MobileEndpoint? _preferred;

    public HubClient(bool allowLoopback = false) => _allowLoopback = allowLoopback;

    /// <summary>The path that answered last, tried first next time.</summary>
    public MobileEndpoint? Preferred
    {
        get { lock (_gate) return _preferred; }
    }

    public Task<HubReply> GetAsync(IReadOnlyList<MobileEndpoint> endpoints, string path, string? token, TimeSpan timeout, CancellationToken ct) =>
        SendAsync(endpoints, HttpMethod.Get, path, null, token, timeout, ct);

    public Task<HubReply> PostAsync<T>(IReadOnlyList<MobileEndpoint> endpoints, string path, T body, string? token, TimeSpan timeout, CancellationToken ct) =>
        SendAsync(endpoints, HttpMethod.Post, path, JsonSerializer.Serialize(body, MobileProtocol.Json), token, timeout, ct);

    public Task<HubReply> DeleteAsync(IReadOnlyList<MobileEndpoint> endpoints, string path, string? token, TimeSpan timeout, CancellationToken ct) =>
        SendAsync(endpoints, HttpMethod.Delete, path, null, token, timeout, ct);

    /// <summary>A raw body (a sealed file chunk, M-65) as <c>application/octet-stream</c>.</summary>
    public Task<HubReply> PostBytesAsync(IReadOnlyList<MobileEndpoint> endpoints, string path, byte[] body, string? token, TimeSpan timeout, CancellationToken ct) =>
        SendAsync(endpoints, HttpMethod.Post, path, null, token, timeout, ct, body);

    private async Task<HubReply> SendAsync(IReadOnlyList<MobileEndpoint> endpoints, HttpMethod method, string path, string? json,
        string? token, TimeSpan timeout, CancellationToken ct, byte[]? bytes = null)
    {
        string? lastError = null;
        foreach (var ep in Order(endpoints))
        {
            using var request = new HttpRequestMessage(method, Url(ep, path));
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (bytes is not null)
            {
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            }
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                using var response = await ClientFor(ep).SendAsync(request, cts.Token);
                if (ep.Transport == MobileTransport.Cloudflare && IsTunnelDown((int)response.StatusCode))
                {
                    lastError = $"{ep.Transport} {ep.Host}: HTTP {(int)response.StatusCode}";
                    continue;
                }
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                lock (_gate) _preferred = ep;
                return new HubReply((int)response.StatusCode, body, ep, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                lastError = $"{ep.Transport} {ep.Host}: {ex.GetBaseException().Message}";
            }
        }
        return new HubReply(0, null, null, lastError ?? "no usable path");
    }

    /// <summary>
    /// Cloudflare's own errors for an origin it can't reach (the Agent answers 503 itself, with a JSON
    /// body, so that one is the PC's answer and is not here).
    /// </summary>
    internal static bool IsTunnelDown(int status) => status is 502 or 504 or (>= 520 and <= 530);

    private IEnumerable<MobileEndpoint> Order(IReadOnlyList<MobileEndpoint> endpoints)
    {
        MobileEndpoint? preferred;
        lock (_gate) preferred = _preferred;
        var usable = HubPaths.Usable(endpoints, _allowLoopback).ToList();
        if (preferred is not null && usable.Remove(preferred)) usable.Insert(0, preferred);
        return usable;
    }

    internal static Uri Url(MobileEndpoint ep, string path)
    {
        var host = IPAddress.TryParse(ep.Host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{ep.Host}]" : ep.Host;
        return new Uri($"{(ep.Tls ? "https" : "http")}://{host}:{ep.Port}{path}");
    }

    private HttpClient ClientFor(MobileEndpoint ep)
    {
        var key = $"{ep.Transport}|{ep.Host}|{ep.Port}|{ep.Tls}|{ep.CertSha256}";
        lock (_gate)
        {
            if (_clients.TryGetValue(key, out var existing)) return existing;
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseProxy = false,
                AllowAutoRedirect = false,
            };
            if (ep.Tls && ep.CertSha256 is { Length: > 0 } pin)
            {
                // Self-signed LAN certificate (M-15): accept exactly the pinned one, nothing else.
                handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && string.Equals(MobileTlsPin(cert), pin, StringComparison.OrdinalIgnoreCase);
            }
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _clients[key] = client;
            return client;
        }
    }

    internal static string MobileTlsPin(X509Certificate cert) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
        }
    }
}
