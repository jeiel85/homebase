using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Push;
using LocalOpsBot.Protocol.Messaging;
using LocalOpsBot.Tests.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Push;

public class UnifiedPushChannelTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryDeviceStore _store = new();
    private readonly CapturingHandler _http = new();
    private readonly ECDiffieHellman _phoneKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _auth = RandomNumberGenerator.GetBytes(16);
    private readonly UnifiedPushOutboundChannel _channel;
    private readonly string _deviceId;

    public UnifiedPushChannelTests()
    {
        _deviceId = _store.Add("Galaxy", "SM-S921N", PairingService.HashToken("t")).DeviceId;
        _channel = new UnifiedPushOutboundChannel(_store, new HttpClient(_http), _time, "HOME-PC");
    }

    private void Subscribe(string endpoint = "https://ntfy.sh/upAbCdEf123?up=1") =>
        _store.SetPushSubscription(_deviceId, new PushSubscription(
            endpoint, Base64Url.Encode(WebPushEncryption.ExportUncompressed(_phoneKey)), Base64Url.Encode(_auth), _time.GetUtcNow()));

    private static OutboundNotification Alert(OutboundPriority priority = OutboundPriority.Critical, string? body = "C: 2% free",
        IReadOnlyDictionary<string, string>? meta = null, string category = "process_watch") =>
        new(Guid.NewGuid(), category, priority, "Disk almost full", body, "k",
            MessageSensitivity.Normal, DeliveryPolicy.TelegramFallback, new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero),
            Metadata: meta);

    [Fact]
    public async Task Not_available_until_a_phone_registers()
    {
        Assert.False(_channel.IsAvailable);
        var result = await _channel.SendAsync(Alert(), default);
        Assert.Equal(OutboundFailureKind.NotConfigured, result.FailureKind);
        Subscribe();
        Assert.True(_channel.IsAvailable);
    }

    [Fact]
    public async Task Posts_an_aes128gcm_body_the_phone_can_decrypt()
    {
        Subscribe();
        var result = await _channel.SendAsync(Alert(), default);

        Assert.True(result.Success);
        var req = Assert.Single(_http.Requests);
        Assert.Equal("https://ntfy.sh/upAbCdEf123?up=1", req.Uri);
        Assert.Equal("aes128gcm", req.ContentEncoding);
        Assert.Equal("high", req.Headers["Urgency"]);
        Assert.Equal("86400", req.Headers["TTL"]);
        Assert.True(req.Body.Length <= WebPushEncryption.MaxBodyBytes);

        var message = JsonSerializer.Deserialize<PushMessage>(WebPushEncryption.Decrypt(req.Body, _phoneKey, _auth), MobileProtocol.Json)!;
        Assert.Equal(MobileProtocol.Schema, message.Schema);
        Assert.Equal("pc-test", message.PcId);
        Assert.Equal("HOME-PC", message.PcName);
        Assert.Equal(PushPriority.Critical, message.Priority);
        Assert.Equal("Disk almost full", message.Title);
        Assert.Equal("process_watch", message.Source);
        Assert.Equal(1, message.Count);
    }

    [Fact]
    public async Task Toasts_carry_their_source_app_and_count_and_a_shorter_ttl()
    {
        Subscribe();
        await _channel.SendAsync(Alert(OutboundPriority.Info, meta: new Dictionary<string, string>
        {
            ["sourceApp"] = "KakaoTalk", ["count"] = "3",
        }, category: "windows-toast"), default);

        var req = Assert.Single(_http.Requests);
        Assert.Equal("normal", req.Headers["Urgency"]);
        Assert.Equal("21600", req.Headers["TTL"]);
        var message = JsonSerializer.Deserialize<PushMessage>(WebPushEncryption.Decrypt(req.Body, _phoneKey, _auth), MobileProtocol.Json)!;
        Assert.Equal("KakaoTalk", message.Source);
        Assert.Equal(3, message.Count);
    }

    [Fact]
    public async Task A_huge_body_is_cut_to_fit_4096_bytes()
    {
        Subscribe();
        var result = await _channel.SendAsync(Alert(body: string.Concat(Enumerable.Repeat("가나다\"<>", 2000))), default);

        Assert.True(result.Success);
        var req = Assert.Single(_http.Requests);
        Assert.True(req.Body.Length <= 4096, $"body was {req.Body.Length} bytes");
        var message = JsonSerializer.Deserialize<PushMessage>(WebPushEncryption.Decrypt(req.Body, _phoneKey, _auth), MobileProtocol.Json)!;
        Assert.EndsWith("…", message.Body);
    }

    [Fact]
    public async Task Rate_limit_honours_retry_after_without_sending_again()
    {
        Subscribe();
        _http.Next = () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return r;
        };

        var first = await _channel.SendAsync(Alert(), default);
        Assert.Equal(OutboundFailureKind.RateLimited, first.FailureKind);

        _http.Next = () => new HttpResponseMessage(HttpStatusCode.Created);
        _time.Advance(TimeSpan.FromSeconds(119));
        var during = await _channel.SendAsync(Alert(), default);
        Assert.Equal(OutboundFailureKind.RateLimited, during.FailureKind);
        Assert.Single(_http.Requests); // the blocked attempt never reached the server

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await _channel.SendAsync(Alert(), default)).Success);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public void Retry_after_defaults_to_60s_and_is_capped_at_an_hour()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.FromSeconds(60), UnifiedPushOutboundChannel.RetryAfter(new HttpResponseMessage(HttpStatusCode.TooManyRequests), now));
        var day = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        day.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(20));
        Assert.Equal(TimeSpan.FromHours(1), UnifiedPushOutboundChannel.RetryAfter(day, now));
        var dated = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        dated.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(30));
        Assert.InRange(UnifiedPushOutboundChannel.RetryAfter(dated, now).TotalSeconds, 29, 31);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_vanished_endpoint_drops_the_subscription(HttpStatusCode status)
    {
        Subscribe();
        _http.Next = () => new HttpResponseMessage(status);

        var result = await _channel.SendAsync(Alert(), default);

        Assert.Equal(OutboundFailureKind.Expired, result.FailureKind);
        Assert.Empty(_store.PushTargets());
        Assert.False(_channel.IsAvailable);
    }

    [Fact]
    public async Task Server_errors_and_network_failures_are_transient()
    {
        Subscribe();
        _http.Next = () => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.Equal(OutboundFailureKind.Transient, (await _channel.SendAsync(Alert(), default)).FailureKind);
        _http.Next = () => throw new HttpRequestException("No such host is known.");
        Assert.Equal(OutboundFailureKind.Transient, (await _channel.SendAsync(Alert(), default)).FailureKind);
        Assert.Single(_store.PushTargets()); // kept: may recover
    }

    [Fact]
    public async Task One_phone_failing_does_not_fail_the_delivery()
    {
        Subscribe("https://ntfy.sh/upGone");
        var second = _store.Add("Tablet", null, PairingService.HashToken("t2")).DeviceId;
        _store.SetPushSubscription(second, new PushSubscription("https://ntfy.sh/upOk",
            Base64Url.Encode(WebPushEncryption.ExportUncompressed(_phoneKey)), Base64Url.Encode(_auth), _time.GetUtcNow()));
        _http.Next = () => _http.Requests[^1].Uri.Contains("upGone")
            ? new HttpResponseMessage(HttpStatusCode.Gone) : new HttpResponseMessage(HttpStatusCode.Created);

        Assert.True((await _channel.SendAsync(Alert(), default)).Success);
        Assert.Equal(2, _http.Requests.Count);
    }

    internal sealed record CapturedRequest(string Uri, string? ContentEncoding, Dictionary<string, string> Headers, byte[] Body);

    internal sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();
        public Func<HttpResponseMessage> Next { get; set; } = () => new HttpResponseMessage(HttpStatusCode.Created);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString(),
                request.Content?.Headers.ContentEncoding.FirstOrDefault(), headers, body));
            return Next();
        }
    }
}

public class PushPayloadTests
{
    private static PushSubscriptionRequest Valid() => new(1, "https://ntfy.sh/upAbc?up=1",
        WebPushEncryptionTests.UaPublic, WebPushEncryptionTests.AuthSecret);

    [Fact]
    public void A_valid_subscription_passes() => Assert.Null(PushPayload.Validate(Valid()));

    [Theory]
    [InlineData("http://ntfy.sh/upAbc")]
    [InlineData("ftp://x/y")]
    [InlineData("https://user:pw@ntfy.sh/up")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Endpoints_must_be_plain_https(string endpoint) =>
        Assert.NotNull(PushPayload.Validate(Valid() with { Endpoint = endpoint }));

    [Fact]
    public void Long_endpoints_wrong_schema_and_bad_keys_are_rejected()
    {
        Assert.NotNull(PushPayload.Validate(Valid() with { Endpoint = "https://ntfy.sh/" + new string('a', 1000) }));
        Assert.NotNull(PushPayload.Validate(Valid() with { Schema = 2 }));
        Assert.NotNull(PushPayload.Validate(Valid() with { P256dh = "AAAA" }));
        Assert.NotNull(PushPayload.Validate(Valid() with { Auth = "!!" }));
        Assert.NotNull(PushPayload.Validate(null));
    }

    [Fact]
    public void Serialize_keeps_small_messages_intact_and_shrinks_big_ones()
    {
        var small = new PushMessage(1, "id", "pc", "PC", "boot", "info", "Booted", "Time: now", "boot", 1, DateTimeOffset.UnixEpoch);
        Assert.Equal(small, JsonSerializer.Deserialize<PushMessage>(PushPayload.Serialize(small), MobileProtocol.Json));

        var huge = small with { Title = new string('제', 3000), Body = new string('목', 3000) };
        var bytes = PushPayload.Serialize(huge);
        Assert.True(bytes.Length <= WebPushEncryption.MaxPlaintextBytes);
        var back = JsonSerializer.Deserialize<PushMessage>(bytes, MobileProtocol.Json)!;
        Assert.EndsWith("…", back.Title);
    }
}
