using System.Security.Cryptography;
using System.Text;

namespace LocalOpsBot.Agent.Services.Mobile;

/// <summary>A parsed HTTP/1.1 request head. Header names are case-insensitive.</summary>
public sealed record HttpRequestHead(string Method, string Path, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public string? BearerToken()
    {
        var auth = Header("Authorization");
        const string prefix = "Bearer ";
        return auth is not null && auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? auth[prefix.Length..].Trim()
            : null;
    }

    public bool IsWebSocketUpgrade =>
        string.Equals(Header("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase) &&
        Header("Sec-WebSocket-Key") is { Length: > 0 };
}

/// <summary>
/// The smallest HTTP/1.1 surface the mobile API needs: one request per connection
/// (<c>Connection: close</c>), bounded head and body, and the RFC 6455 upgrade handshake.
///
/// Why not HttpListener/Kestrel (DECISIONS M-6): http.sys opens the port on every interface and
/// routes by Host header, and its traffic belongs to the System process so an exe-scoped
/// firewall rule can't fence it. A socket bound to the tailnet IP can be; Kestrel would add the
/// ASP.NET runtime to an always-on service for four routes.
/// </summary>
public static class MiniHttp
{
    public const int MaxHeadBytes = 8 * 1024;
    public const int MaxBodyBytes = 4 * 1024;

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>
    /// Reads the request head (up to the blank line). Returns the head and any body bytes that
    /// arrived in the same reads, or <c>null</c> when the head is malformed or too large.
    /// </summary>
    public static async Task<(HttpRequestHead Head, byte[] Leftover)?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0) return null;
            filled += read;
            var end = IndexOfHeadEnd(buffer.AsSpan(0, filled));
            if (end >= 0)
            {
                var head = Parse(Encoding.ASCII.GetString(buffer, 0, end));
                if (head is null) return null;
                return (head, buffer.AsSpan(end + 4, filled - end - 4).ToArray());
            }
        }
        return null; // head larger than MaxHeadBytes
    }

    public static HttpRequestHead? Parse(string head)
    {
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var path = requestLine[1];
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new HttpRequestHead(requestLine[0].ToUpperInvariant(), path, headers);
    }

    /// <summary>Reads exactly Content-Length body bytes (bounded), using bytes already buffered first.</summary>
    public static async Task<byte[]?> ReadBodyAsync(Stream stream, HttpRequestHead head, byte[] leftover, CancellationToken ct)
    {
        if (!int.TryParse(head.Header("Content-Length"), out var length) || length < 0 || length > MaxBodyBytes)
            return null;
        var body = new byte[length];
        var have = Math.Min(leftover.Length, length);
        Array.Copy(leftover, body, have);
        if (have < length) await stream.ReadExactlyAsync(body.AsMemory(have), ct);
        return body;
    }

    public static async Task WriteResponseAsync(Stream stream, int status, string? jsonBody, CancellationToken ct)
    {
        var body = jsonBody is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(jsonBody);
        var sb = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");
        if (jsonBody is not null) sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task WriteUpgradeAsync(Stream stream, string secWebSocketKey, CancellationToken ct)
    {
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: " + WebSocketAccept(secWebSocketKey) + "\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        await stream.FlushAsync(ct);
    }

    public static string WebSocketAccept(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));

    private static int IndexOfHeadEnd(ReadOnlySpan<byte> data) => data.IndexOf("\r\n\r\n"u8);

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        410 => "Gone",
        429 => "Too Many Requests",
        503 => "Service Unavailable",
        _ => "Error",
    };
}
