using System.Security.Cryptography;
using System.Text;

namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// The one request the Tray's hook listener understands (DECISIONS M-48): <c>POST /claude/permission</c>
/// from <c>curl.exe</c>, with our token header and a JSON body of at most 1 MiB. Everything else gets
/// an error status and no body, which Claude Code reads as "no decision".
/// </summary>
public static class ClaudeHookHttp
{
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>Status 200 with the body, or the error status (and a null body) to answer with.</summary>
    public static async Task<(int Status, string? Body)> ReadRequestAsync(Stream stream, string token, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var read = 0;
        int end;
        while ((end = IndexOfHeaderEnd(buffer, read)) < 0)
        {
            if (read == buffer.Length) return (431, null);
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return (400, null);
            read += n;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2 || requestLine[0] != "POST" || requestLine[1] != ClaudeHook.Path) return (404, null);
        string? given = null;
        var length = -1;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals(ClaudeHook.TokenHeader, StringComparison.OrdinalIgnoreCase)) given = value;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var l)) length = l;
        }
        if (given is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token)))
            return (403, null);
        if (length < 0 || length > MaxBodyBytes) return (413, null);

        var body = new byte[length];
        var already = Math.Min(read - (end + 4), length);
        Array.Copy(buffer, end + 4, body, 0, already);
        var got = already;
        while (got < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(got), ct);
            if (n == 0) return (400, null);
            got += n;
        }
        return (200, Encoding.UTF8.GetString(body));
    }

    public static async Task WriteResponseAsync(Stream stream, int status, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\n" +
                   $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
    }

    private static int IndexOfHeaderEnd(byte[] b, int count)
    {
        for (var i = 3; i < count; i++)
            if (b[i - 3] == '\r' && b[i - 2] == '\n' && b[i - 1] == '\r' && b[i] == '\n') return i - 3;
        return -1;
    }
}
