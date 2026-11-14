using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalOpsBot.Infrastructure.Push;

/// <summary>
/// Sends notifications to every paired phone that registered a UnifiedPush endpoint
/// (<c>POST /api/v1/push</c>). The body is RFC 8291 <c>aes128gcm</c>, so the push server
/// (ntfy.sh or another distributor) relays ciphertext only (DECISIONS M-19).
/// <list type="bullet">
/// <item>429: the endpoint is skipped until its <c>Retry-After</c> (default 60 s, at most 1 h) passes.</item>
/// <item>404/410: the endpoint no longer exists; the subscription is dropped (the app re-registers on its next connect).</item>
/// <item>Success when at least one phone accepted the message.</item>
/// </list>
/// Sending never touches the live sampler, so it does not break "collect only while watched" (M-13).
/// </summary>
public sealed class UnifiedPushOutboundChannel : IOutboundChannel
{
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    private readonly IMobileDeviceStore _devices;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly string _pcName;
    private readonly ILogger _logger;
    private readonly Dictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public UnifiedPushOutboundChannel(
        IMobileDeviceStore devices,
        HttpClient http,
        TimeProvider time,
        string pcName,
        ILogger<UnifiedPushOutboundChannel>? logger = null)
    {
        _devices = devices;
        _http = http;
        _time = time;
        _pcName = pcName;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string ChannelId => ChannelIds.UnifiedPush;

    public bool IsAvailable => _devices.PushTargets().Count > 0;

    public async Task<OutboundAttemptResult> SendAsync(OutboundNotification notification, CancellationToken ct)
    {
        var targets = _devices.PushTargets();
        if (targets.Count == 0)
            return new OutboundAttemptResult(ChannelId, false, OutboundFailureKind.NotConfigured, "No phone registered for push.");

        var plaintext = PushPayload.Serialize(ToMessage(notification));
        var failures = new List<(OutboundFailureKind Kind, string Error)>();
        var delivered = 0;

        foreach (var target in targets)
        {
            var (ok, kind, error) = await SendOneAsync(target, plaintext, TtlSeconds(notification), Urgency(notification.Priority), ct);
            if (ok) delivered++;
            else failures.Add((kind, $"{target.DeviceName}: {error}"));
        }

        if (delivered > 0)
        {
            foreach (var f in failures)
                _logger.LogWarning("Push to one phone failed ({Kind}): {Error}", f.Kind, f.Error);
            return new OutboundAttemptResult(ChannelId, true);
        }

        // Report the most actionable failure kind; the router falls back on any failure.
        var worst = failures.Select(f => f.Kind).OrderBy(Rank).First();
        return new OutboundAttemptResult(ChannelId, false, worst, string.Join("; ", failures.Select(f => f.Error)));
    }

    /// <summary>Devices that can receive a path update: a push subscription and a token hash to sign with.</summary>
    public IReadOnlyList<string> EndpointTargetIds() =>
        _devices.PushTargets().Where(t => !string.IsNullOrEmpty(t.TokenSha256)).Select(t => t.DeviceId).ToList();

    /// <summary>
    /// Tells the phones in <paramref name="deviceIds"/> this PC's current paths (DECISIONS M-23), signed
    /// per device with its token hash. Shows nothing on the phone. Returns the devices that accepted it.
    /// </summary>
    public async Task<IReadOnlyList<string>> SendEndpointsAsync(
        IReadOnlyList<MobileEndpoint> endpoints, IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        var delivered = new List<string>();
        var ts = _time.GetUtcNow();
        foreach (var target in _devices.PushTargets())
        {
            if (string.IsNullOrEmpty(target.TokenSha256) || !deviceIds.Contains(target.DeviceId)) continue;
            byte[] plaintext;
            try
            {
                plaintext = PushEndpoints.Serialize(_devices.PcId, endpoints, ts, target.TokenSha256);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                _logger.LogError(ex, "Endpoint update to {Device} could not be built", target.DeviceName);
                continue;
            }
            var (ok, kind, error) = await SendOneAsync(target, plaintext, EndpointsTtlSeconds, "normal", ct);
            if (ok) delivered.Add(target.DeviceId);
            else _logger.LogWarning("Endpoint update to {Device} failed ({Kind}): {Error}", target.DeviceName, kind, error);
        }
        return delivered;
    }

    /// <summary>Paths stay valid until the next restart; a day covers a phone that was off overnight.</summary>
    internal const int EndpointsTtlSeconds = 24 * 3600;

    private async Task<(bool Ok, OutboundFailureKind Kind, string? Error)> SendOneAsync(
        PushTarget target, byte[] plaintext, int ttlSeconds, string urgency, CancellationToken ct)
    {
        var endpoint = target.Subscription.Endpoint;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_blockedUntil.TryGetValue(endpoint, out var until))
            {
                if (now < until)
                    return (false, OutboundFailureKind.RateLimited,
                        $"rate limited for {(int)Math.Ceiling((until - now).TotalSeconds)}s more");
                _blockedUntil.Remove(endpoint);
            }
        }

        byte[] body;
        try
        {
            body = WebPushEncryption.Encrypt(
                plaintext,
                Base64Url.Decode(target.Subscription.P256dh),
                Base64Url.Decode(target.Subscription.Auth));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // Keys were validated on registration; a stored bad key cannot recover by retrying.
            return (false, OutboundFailureKind.Permanent, "stored subscription keys are invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", ttlSeconds.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", urgency);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, OutboundFailureKind.Transient, ex.Message);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return (true, OutboundFailureKind.None, null);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryAfter(response, _time.GetUtcNow());
                lock (_gate) _blockedUntil[endpoint] = _time.GetUtcNow() + wait;
                _logger.LogWarning("Push endpoint rate limited (429); pausing it for {Seconds}s", (int)wait.TotalSeconds);
                return (false, OutboundFailureKind.RateLimited, $"HTTP 429, retry after {(int)wait.TotalSeconds}s");
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                DropIfUnchanged(target);
                return (false, OutboundFailureKind.Expired, $"HTTP {status}: endpoint no longer exists");
            }

            if (status is 401 or 403)
                return (false, OutboundFailureKind.Unauthorized, $"HTTP {status}");
            if (status is 400 or 413)
                return (false, OutboundFailureKind.Permanent, $"HTTP {status}");
            return (false, OutboundFailureKind.Transient, $"HTTP {status}");
        }
    }

    private void DropIfUnchanged(PushTarget target)
    {
        var current = _devices.PushTargets().FirstOrDefault(t => t.DeviceId == target.DeviceId);
        if (current is null || current.Subscription.Endpoint != target.Subscription.Endpoint) return;
        _devices.SetPushSubscription(target.DeviceId, null);
        _logger.LogWarning("Push endpoint of {Device} is gone; dropped the subscription until the app registers again", target.DeviceName);
    }

    private PushMessage ToMessage(OutboundNotification n)
    {
        string? Meta(string key) => n.Metadata is not null && n.Metadata.TryGetValue(key, out var v) ? v : null;
        var count = int.TryParse(Meta("count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : 1;
        return new PushMessage(
            MobileProtocol.Schema,
            n.NotificationId.ToString("N"),
            _devices.PcId,
            _pcName,
            n.Category,
            n.Priority switch
            {
                OutboundPriority.Critical => PushPriority.Critical,
                OutboundPriority.Warning => PushPriority.Warning,
                OutboundPriority.Recovery => PushPriority.Recovery,
                _ => PushPriority.Info,
            },
            n.Title,
            string.IsNullOrEmpty(n.Body) ? null : n.Body,
            Meta("sourceApp") ?? n.Category,
            count,
            n.CreatedAt);
    }

    /// <summary>Forwarded toasts go stale quickly; alerts stay relevant for a day.</summary>
    internal static int TtlSeconds(OutboundNotification n)
    {
        var ttl = n.Category == "windows-toast" ? 6 * 3600 : 24 * 3600;
        if (n.ExpiresAt is { } exp)
            ttl = (int)Math.Clamp((exp - DateTimeOffset.UtcNow).TotalSeconds, 0, ttl);
        return ttl;
    }

    private static string Urgency(OutboundPriority priority) =>
        priority is OutboundPriority.Critical or OutboundPriority.Warning ? "high" : "normal";

    internal static TimeSpan RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is { } date ? date - now : null);
        if (wait is null || wait <= TimeSpan.Zero) return DefaultRetryAfter;
        return wait > MaxRetryAfter ? MaxRetryAfter : wait.Value;
    }

    private static int Rank(OutboundFailureKind kind) => kind switch
    {
        OutboundFailureKind.RateLimited => 0,
        OutboundFailureKind.Transient => 1,
        OutboundFailureKind.Expired => 2,
        OutboundFailureKind.Unauthorized => 3,
        OutboundFailureKind.Permanent => 4,
        _ => 5,
    };
}
