using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Push;
using LocalOpsBot.Infrastructure.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>DECISIONS M-48: Claude Code permission prompts answered from the phone.</summary>
public class ClaudeApprovalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 1, 0, 0, TimeSpan.Zero);

    // ---- hook input / output -------------------------------------------------------------------

    private const string BashInput = """
        {"session_id":"a26bf009-27c0-456f-a353-6b48a3d785e2","transcript_path":"C:\\x.jsonl","cwd":"D:\\Project\\homebase",
         "hook_event_name":"PermissionRequest","tool_name":"Bash",
         "tool_input":{"command":"git push origin main","description":"Push the release"}}
        """;

    [Fact]
    public void A_bash_prompt_reads_as_what_it_would_run_in_which_session()
    {
        var r = ClaudeHook.FromInput(BashInput, id => id.StartsWith("a26b") ? "알림 개선" : null, T0)!;
        Assert.Equal("Claude Code", r.App);
        Assert.Equal("알림 개선", r.Session);
        Assert.Equal(@"D:\Project\homebase", r.Project);
        Assert.Equal("명령 실행", r.ToolTitle);
        Assert.Equal("Push the release\n\ngit push origin main", r.Detail);
        Assert.Equal(T0 + ClaudeHook.WaitLimit, r.ExpiresAt);
        Assert.Matches("^[0-9a-f]{32}$", r.Id);
    }

    [Fact]
    public void Without_a_known_session_the_folder_names_it()
    {
        var r = ClaudeHook.FromInput(BashInput, _ => null, T0)!;
        Assert.Equal("homebase", r.Session);
    }

    [Theory]
    [InlineData("""{"tool_name":"AskUserQuestion","tool_input":{"questions":[]}}""")]
    [InlineData("""{"tool_input":{}}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Questions_and_unreadable_input_leave_the_pc_to_ask(string json) =>
        Assert.Null(ClaudeHook.FromInput(json, _ => null, T0));

    [Fact]
    public void Edits_show_the_file_and_the_start_of_the_new_text_and_long_details_are_cut()
    {
        var edit = JsonDocument.Parse("""{"file_path":"C:\\a.cs","old_string":"x","new_string":"1\n2\n3"}""").RootElement;
        Assert.Equal("C:\\a.cs\n\n1\n2\n3", ClaudeHook.Detail("Edit", edit));
        var write = JsonDocument.Parse(JsonSerializer.Serialize(new { file_path = "C:\\b.txt", content = string.Join('\n', Enumerable.Range(1, 20)) })).RootElement;
        Assert.EndsWith("12\n… (8줄 더)", ClaudeHook.Detail("Write", write));
        var big = JsonDocument.Parse(JsonSerializer.Serialize(new { command = new string('x', 5000) })).RootElement;
        Assert.Equal(ClaudeHook.MaxDetail + 1, ClaudeHook.Detail("Bash", big).Length);
        Assert.Contains("\"q\": \"1\"", ClaudeHook.Detail("mcp__x__y", JsonDocument.Parse("""{"q":"1"}""").RootElement));
        Assert.Equal("github › create_pr 사용", ClaudeHook.ToolTitle("mcp__github__create_pr"));
    }

    [Fact]
    public void The_output_is_claude_codes_permission_decision()
    {
        var allow = JsonNode.Parse(ClaudeHook.Output(ApprovalDecision.Allow))!;
        Assert.Equal("PermissionRequest", (string?)allow["hookSpecificOutput"]!["hookEventName"]);
        Assert.Equal("allow", (string?)allow["hookSpecificOutput"]!["decision"]!["behavior"]);
        var deny = JsonNode.Parse(ClaudeHook.Output(ApprovalDecision.Deny))!;
        Assert.Equal("deny", (string?)deny["hookSpecificOutput"]!["decision"]!["behavior"]);
        Assert.False(string.IsNullOrEmpty((string?)deny["hookSpecificOutput"]!["decision"]!["message"]));
    }

    // ---- settings.json -------------------------------------------------------------------------

    private const string UserSettings = """
        {
          // the user's own comment
          "model": "opus",
          "hooks": {
            "PermissionRequest": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "my-own-hook.exe" } ] } ],
            "Stop": [ { "hooks": [ { "type": "command", "command": "notify.exe" } ] } ]
          },
        }
        """;

    [Fact]
    public void Installing_adds_one_hook_and_keeps_the_users_settings()
    {
        var cmd = ClaudeHook.Command(47391, "t0ken0000000000000000000000000000");
        var once = ClaudeHook.ApplyToSettings(UserSettings, cmd);
        var twice = ClaudeHook.ApplyToSettings(once, ClaudeHook.Command(47392, "t0ken0000000000000000000000000000"));
        var root = JsonNode.Parse(twice)!;

        Assert.Equal("opus", (string?)root["model"]);
        Assert.Equal("notify.exe", (string?)root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]);
        var groups = root["hooks"]!["PermissionRequest"]!.AsArray();
        Assert.Equal(2, groups.Count); // the user's and ours (replaced, not added twice)
        Assert.Equal("my-own-hook.exe", (string?)groups[0]!["hooks"]![0]!["command"]);
        Assert.Contains("127.0.0.1:47392/claude/permission", (string?)groups[1]!["hooks"]![0]!["command"]);
        Assert.Equal(ClaudeHook.TimeoutSeconds, (int)groups[1]!["hooks"]![0]!["timeout"]!);
        Assert.Equal(ClaudeHook.Command(47392, "t0ken0000000000000000000000000000"), ClaudeHook.InstalledCommand(twice));
    }

    [Fact]
    public void Removing_takes_out_only_our_hook_and_empty_containers()
    {
        var installed = ClaudeHook.ApplyToSettings(UserSettings, ClaudeHook.Command(47391, "t"));
        var removed = JsonNode.Parse(ClaudeHook.ApplyToSettings(installed, null))!;
        Assert.Single(removed["hooks"]!["PermissionRequest"]!.AsArray());
        Assert.Null(ClaudeHook.InstalledCommand(removed.ToJsonString()));

        var alone = ClaudeHook.ApplyToSettings(null, ClaudeHook.Command(47391, "t"));
        Assert.Equal("{}", ClaudeHook.ApplyToSettings(alone, null).Replace("\r", "").Replace("\n", "").Replace(" ", ""));
        Assert.Null(ClaudeHook.InstalledCommand(null));
        Assert.Throws<InvalidDataException>(() => ClaudeHook.ApplyToSettings("[1]", null));
    }

    [Fact]
    public void The_command_is_curl_to_loopback_with_the_token() =>
        Assert.Equal(
            "curl.exe -s --max-time 630 -X POST http://127.0.0.1:47391/claude/permission -H \"X-Homebase-Token: abc\" " +
            "-H \"Content-Type: application/json\" --data-binary \"@-\"",
            ClaudeHook.Command(47391, "abc"));

    // ---- the Tray's listener ---------------------------------------------------------------------

    private static Task<(int Status, string? Body)> Read(string raw, string token = "tok") =>
        ClaudeHookHttp.ReadRequestAsync(new MemoryStream(Encoding.UTF8.GetBytes(raw)), token, CancellationToken.None);

    [Fact]
    public async Task Only_our_path_with_our_token_and_a_sane_length_is_read()
    {
        const string body = """{"tool_name":"Bash"}""";
        var ok = $"POST /claude/permission HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Homebase-Token: tok\r\nContent-Length: {body.Length}\r\n\r\n{body}";
        Assert.Equal((200, body), await Read(ok));
        Assert.Equal(403, (await Read(ok.Replace("X-Homebase-Token: tok", "X-Homebase-Token: other"))).Status);
        Assert.Equal(403, (await Read(ok.Replace("X-Homebase-Token: tok\r\n", ""))).Status);
        Assert.Equal(404, (await Read(ok.Replace("POST /claude/permission", "POST /api/v1/approval"))).Status);
        Assert.Equal(404, (await Read(ok.Replace("POST ", "GET "))).Status);
        Assert.Equal(413, (await Read(ok.Replace($"Content-Length: {body.Length}", "Content-Length: 99999999"))).Status);
        Assert.Equal(400, (await Read(ok[..^5])).Status); // body cut short
        Assert.Equal(431, (await Read("POST /claude/permission HTTP/1.1\r\nX: " + new string('a', ClaudeHookHttp.MaxHeaderBytes))).Status);
    }

    [Fact]
    public async Task A_real_http_client_round_trips_through_the_listener_code()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var (status, body) = await ClaudeHookHttp.ReadRequestAsync(stream, "tok", CancellationToken.None);
            var reply = status == 200 && ClaudeHook.FromInput(body!, _ => "세션", T0) is not null ? ClaudeHook.Output(ApprovalDecision.Allow) : "";
            await ClaudeHookHttp.WriteResponseAsync(stream, status, reply, CancellationToken.None);
        });
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{ClaudeHook.Path}")
        {
            Content = new StringContent(BashInput, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ClaudeHook.TokenHeader, "tok");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        await serve;
        listener.Stop();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("allow", (string?)JsonNode.Parse(text)!["hookSpecificOutput"]!["decision"]!["behavior"]);
    }

    // ---- broker --------------------------------------------------------------------------------

    private sealed class FakeRouter(bool delivered) : IOutboundRouter
    {
        public List<OutboundNotification> Sent { get; } = new();
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OutboundDeliveryResult> DeliverAsync(OutboundNotification n, CancellationToken ct)
        {
            Sent.Add(n);
            Delivered.TrySetResult();
            return Task.FromResult(new OutboundDeliveryResult(delivered,
                [new OutboundAttemptResult("unifiedpush", delivered, delivered ? OutboundFailureKind.None : OutboundFailureKind.NotConfigured, delivered ? null : "no phone")]));
        }
    }

    private static ApprovalRequest Request(DateTimeOffset now) =>
        new(ClaudeHook.NewId(), "Claude Code", "알림 개선", @"D:\p", "명령 실행", "git push", now + ClaudeHook.WaitLimit);

    [Fact]
    public async Task The_first_phone_answer_goes_back_to_the_hook()
    {
        var router = new FakeRouter(true);
        var broker = new ApprovalBroker(NullLogger<ApprovalBroker>.Instance, router);
        var r = Request(DateTimeOffset.UtcNow);
        var ask = broker.AskAsync(r, CancellationToken.None);
        await router.Delivered.Task;

        Assert.Equal(1, broker.PendingCount);
        Assert.Equal(ApprovalAnswerOutcome.Invalid, broker.Answer(r.Id, "maybe", "S24"));
        Assert.Equal(ApprovalAnswerOutcome.Accepted, broker.Answer(r.Id, ApprovalDecision.Deny, "S24"));
        Assert.Equal(ApprovalAnswerOutcome.Unknown, broker.Answer(r.Id, ApprovalDecision.Allow, "Tab"));
        Assert.Equal(ApprovalDecision.Deny, await ask);
        Assert.Equal(0, broker.PendingCount);

        var push = Assert.Single(router.Sent);
        Assert.Equal(ClaudeHook.Category, push.Category);
        Assert.Equal(OutboundPriority.Critical, push.Priority);
        Assert.Equal(DeliveryPolicy.EncryptedOnly, push.Policy);
        Assert.Equal("명령 실행", push.Title);
        Assert.Equal("알림 개선\ngit push", push.Body);
        Assert.Equal(r.Id, push.Metadata![ClaudeHook.MetaApprovalId]);
        Assert.True(broker.RecentlyAsked("Claude Code", "알림 개선"));
        Assert.False(broker.RecentlyAsked("Claude Code", "다른 세션"));
    }

    [Fact]
    public async Task Undelivered_withdrawn_or_expired_prompts_are_left_to_the_pc()
    {
        var notDelivered = new ApprovalBroker(NullLogger<ApprovalBroker>.Instance, new FakeRouter(false));
        Assert.Null(await notDelivered.AskAsync(Request(DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.Equal(0, notDelivered.PendingCount);

        var router = new FakeRouter(true);
        var broker = new ApprovalBroker(NullLogger<ApprovalBroker>.Instance, router);
        var r = Request(DateTimeOffset.UtcNow);
        var ask = broker.AskAsync(r, CancellationToken.None);
        await router.Delivered.Task;
        broker.Cancel(r.Id);
        Assert.Null(await ask);
        Assert.Equal(ApprovalAnswerOutcome.Unknown, broker.Answer(r.Id, ApprovalDecision.Allow, "S24"));

        var soon = Request(DateTimeOffset.UtcNow) with { ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(300) };
        Assert.Null(await new ApprovalBroker(NullLogger<ApprovalBroker>.Instance, new FakeRouter(true)).AskAsync(soon, CancellationToken.None));
        Assert.Null(await new ApprovalBroker(NullLogger<ApprovalBroker>.Instance).AskAsync(Request(DateTimeOffset.UtcNow), CancellationToken.None));
    }

    [Fact]
    public async Task Prompts_no_phone_takes_go_back_to_the_pc_at_once()
    {
        // The push channel counts a muted category as delivered (M-25): without this the hook waited 10 minutes (#64).
        var store = new InMemoryDeviceStore();
        var a = store.Add("S24", null, "aa");
        var b = store.Add("Tab", null, "bb");
        PushSubscription Sub(params string[] muted) => new("https://push.example/x", "p", "a", DateTimeOffset.UnixEpoch, muted);
        store.SetPushSubscription(a.DeviceId, Sub(ClaudeHook.Category));
        store.SetPushSubscription(b.DeviceId, Sub(ClaudeHook.Category, "agent-done"));
        var router = new FakeRouter(true);
        var broker = new ApprovalBroker(NullLogger<ApprovalBroker>.Instance, router, devices: store);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await broker.AskAsync(Request(DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
        Assert.Empty(router.Sent);

        // One phone that still takes them is enough.
        store.SetPushSubscription(b.DeviceId, Sub("agent-done"));
        var r = Request(DateTimeOffset.UtcNow);
        var ask = broker.AskAsync(r, CancellationToken.None);
        await router.Delivered.Task;
        broker.Cancel(r.Id);
        Assert.Null(await ask);
        Assert.Single(router.Sent);
    }

    private sealed class SlowRouter(TimeSpan delay) : IOutboundRouter
    {
        public async Task<OutboundDeliveryResult> DeliverAsync(OutboundNotification n, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new OutboundDeliveryResult(true, [new OutboundAttemptResult("unifiedpush", true)]);
        }
    }

    [Fact]
    public async Task Slow_delivery_does_not_lengthen_the_window()
    {
        // The deadline counts from the ask, delivery included (Codex review on #63).
        var broker = new ApprovalBroker(NullLogger<ApprovalBroker>.Instance, new SlowRouter(TimeSpan.FromSeconds(5)));
        var r = Request(DateTimeOffset.UtcNow) with { ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(300) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await broker.AskAsync(r, CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task An_answer_after_the_deadline_is_refused_even_before_the_timer_fires()
    {
        var time = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        var router = new FakeRouter(true);
        var broker = new ApprovalBroker(NullLogger<ApprovalBroker>.Instance, router, time);
        var r = Request(time.Now);
        var ask = broker.AskAsync(r, CancellationToken.None);
        await router.Delivered.Task;

        time.Advance(ClaudeHook.WaitLimit + TimeSpan.FromSeconds(1));
        Assert.Equal(ApprovalAnswerOutcome.Unknown, broker.Answer(r.Id, ApprovalDecision.Allow, "S24"));
        broker.Cancel(r.Id);
        Assert.Null(await ask);
    }

    [Fact]
    public async Task Too_many_open_prompts_are_not_pushed()
    {
        var router = new FakeRouter(true);
        var broker = new ApprovalBroker(NullLogger<ApprovalBroker>.Instance, router);
        var open = Enumerable.Range(0, ApprovalBroker.MaxPending).Select(_ => broker.AskAsync(Request(DateTimeOffset.UtcNow), CancellationToken.None)).ToList();
        while (broker.PendingCount < ApprovalBroker.MaxPending) await Task.Delay(10);
        Assert.Null(await broker.AskAsync(Request(DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.Equal(ApprovalBroker.MaxPending, router.Sent.Count);
        using var cts = new CancellationTokenSource();
        foreach (var n in router.Sent) broker.Cancel(n.Metadata![ClaudeHook.MetaApprovalId]);
        await Task.WhenAll(open);
    }

    // ---- API -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_paired_phone_answers_over_the_api()
    {
        var store = new InMemoryDeviceStore();
        var pairing = new PairingService(store, new ManualTimeProvider());
        var options = Options.Create(new MobileOptions { Enabled = true });
        var hub = new LiveSnapshotHub(new FakeSampler(), options, TimeProvider.System, NullLogger<LiveSnapshotHub>.Instance);
        var router = new FakeRouter(true);
        var broker = new ApprovalBroker(NullLogger<ApprovalBroker>.Instance, router);
        var api = new MobileApiServer(hub, pairing, store,
            new TailscaleAddressResolver(NullLogger<TailscaleAddressResolver>.Instance),
            new LanAddressResolver(NullLogger<LanAddressResolver>.Instance), options,
            NullLogger<MobileApiServer>.Instance, approvals: broker);
        var (code, _) = pairing.Begin();
        var paired = JsonSerializer.Deserialize<PairResponse>((await api.HandleRestAsync(Req("POST", "/api/v1/pair"),
            Body(new PairRequest(code, "S24", null)), CancellationToken.None)).Json, MobileProtocol.Json)!;

        var r = Request(DateTimeOffset.UtcNow);
        var ask = broker.AskAsync(r, CancellationToken.None);
        await router.Delivered.Task;

        Assert.Equal(401, (await api.HandleRestAsync(Req("POST", "/api/v1/approval", "nope"),
            Body(new ApprovalAnswer(1, r.Id, "allow")), CancellationToken.None)).Status);
        Assert.Equal(400, (await api.HandleRestAsync(Req("POST", "/api/v1/approval", paired.Token),
            Encoding.UTF8.GetBytes("{bad"), CancellationToken.None)).Status);
        Assert.Equal(200, (await api.HandleRestAsync(Req("POST", "/api/v1/approval", paired.Token),
            Body(new ApprovalAnswer(1, r.Id, "allow")), CancellationToken.None)).Status);
        Assert.Equal(ApprovalDecision.Allow, await ask);
        Assert.Equal(410, (await api.HandleRestAsync(Req("POST", "/api/v1/approval", paired.Token),
            Body(new ApprovalAnswer(1, r.Id, "deny")), CancellationToken.None)).Status);
        Assert.Equal(405, (await api.HandleRestAsync(Req("GET", "/api/v1/approval", paired.Token), null, CancellationToken.None)).Status);
    }

    [Fact]
    public void The_bridge_accepts_approval_ops_from_the_tray()
    {
        Assert.Contains(MobileBridge.OpApprovalAsk, MobileBridge.AllowedTrayOps);
        Assert.Contains(MobileBridge.OpApprovalCancel, MobileBridge.AllowedTrayOps);
    }

    private static HttpRequestHead Req(string method, string path, string? token = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token is not null) headers["Authorization"] = "Bearer " + token;
        return new HttpRequestHead(method, path, headers);
    }

    private static byte[] Body<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, MobileProtocol.Json);
}
