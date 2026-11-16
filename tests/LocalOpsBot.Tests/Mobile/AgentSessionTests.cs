using System.Text.Json;
using LocalOpsBot.Core.Delivery;
using LocalOpsBot.Core.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>DECISIONS M-44 (session detail) and M-45 (waiting / done alerts).</summary>
public class AgentSessionTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 14, 0, 0, TimeSpan.Zero);

    // ---- Claude Code status files -------------------------------------------------------------

    private const string StatusFile = """
        {"pid":420,"sessionId":"a26bf009-27c0-456f-a353-6b48a3d785e2","cwd":"D:\\Project\\homebase","startedAt":1794804808841,
         "procStart":"134392784070293210","version":"2.1.325","kind":"interactive","entrypoint":"claude-desktop",
         "messagingSocketPath":"\\\\.\\pipe\\LOCAL\\cc-msg-x","name":"Phonebox 기능 정리","updatedAt":1794837036546,
         "status":"waiting","waitingFor":"Bash 실행 승인","statusUpdatedAt":1794837036546,"bridgeSessionId":"session_01KuE5FPMg4WPj9yciyPDgKg"}
        """;

    [Fact]
    public void A_status_file_gives_status_reason_and_the_claude_ai_link()
    {
        long? seenStart = null;
        var s = ClaudeStatusReader.Parse(StatusFile, (pid, start) => { seenStart = start; return pid == 420; });

        Assert.NotNull(s);
        Assert.Equal(134392784070293210L, seenStart);
        Assert.Equal("a26bf009-27c0-456f-a353-6b48a3d785e2", s!.SessionId);
        Assert.Equal("Phonebox 기능 정리", s.Name);
        Assert.Equal(AgentStatus.Waiting, s.Status);
        Assert.Equal("Bash 실행 승인", s.WaitingFor);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794837036546), s.StatusSince);
        Assert.Equal("https://claude.ai/code/session_01KuE5FPMg4WPj9yciyPDgKg", s.RemoteUrl);
        Assert.DoesNotContain("pipe", s.ToString());
    }

    [Fact]
    public void A_status_file_of_a_finished_process_or_a_reused_pid_is_skipped() =>
        Assert.Null(ClaudeStatusReader.Parse(StatusFile, (_, _) => false));

    [Theory]
    [InlineData("busy", AgentStatus.Busy)]
    [InlineData("shell", AgentStatus.Busy)]
    [InlineData("waiting", AgentStatus.Waiting)]
    [InlineData("idle", AgentStatus.Idle)]
    [InlineData(null, AgentStatus.Idle)]
    public void Claude_statuses_map_to_three(string? raw, string expected) =>
        Assert.Equal(expected, ClaudeStatusReader.NormalizeStatus(raw));

    [Fact]
    public void The_waiting_reason_is_dropped_once_the_session_works_again_and_odd_links_are_not_built()
    {
        var json = """{"pid":1,"sessionId":"s","status":"busy","waitingFor":"old","bridgeSessionId":"../evil"}""";
        var s = ClaudeStatusReader.Parse(json, (_, _) => true)!;
        Assert.Null(s.WaitingFor);
        Assert.Null(s.RemoteUrl);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"pid":0,"sessionId":"s"}""")]
    [InlineData("""{"pid":5}""")]
    [InlineData("[1,2]")]
    public void Unusable_status_files_are_skipped(string json) =>
        Assert.Null(ClaudeStatusReader.Parse(json, (_, _) => true));

    [Fact]
    public void The_status_folder_is_read_file_by_file()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hb-claude-st-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "420.json"), StatusFile);
            File.WriteAllText(Path.Combine(dir, "7.json"), """{"pid":7,"sessionId":"gone","status":"busy"}""");
            File.WriteAllText(Path.Combine(dir, "8.json"), "{half");
            var list = new ClaudeStatusReader(dir, (pid, _) => pid == 420).Read();
            Assert.Equal("Phonebox 기능 정리", Assert.Single(list).Name);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- Claude Code transcripts ---------------------------------------------------------------

    private static string Assistant(string model, int input, int cacheWrite, int cacheRead, string? tool, bool sidechain = false) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            isSidechain = sidechain,
            message = new
            {
                model,
                usage = new { input_tokens = input, cache_creation_input_tokens = cacheWrite, cache_read_input_tokens = cacheRead, output_tokens = 9 },
                content = tool is null
                    ? new object[] { new { type = "text", text = "SECRET REPLY" } }
                    : new object[] { new { type = "text", text = "SECRET REPLY" }, new { type = "tool_use", name = tool, input = new { command = "SECRET ARG" } } },
            },
        });

    [Fact]
    public void The_newest_assistant_entry_gives_model_context_and_last_tool_without_any_text()
    {
        var tail = string.Join('\n',
            "{\"partial",
            """{"type":"custom-title","customTitle":"알림 개선"}""",
            """{"type":"pr-link","prNumber":56,"prUrl":"https://github.com/x/y/pull/56"}""",
            """{"type":"mode","mode":"plan"}""",
            Assistant("claude-opus-5-5", 2, 3000, 180000, "Bash"),
            """{"type":"user","cwd":"D:\\Project\\homebase","message":{"content":"SECRET PROMPT"}}""",
            Assistant("<synthetic>", 0, 0, 0, null),
            Assistant("claude-haiku-5-5", 1, 1, 1, "Read", sidechain: true),
            Assistant("claude-opus-5-5", 3, 500, 184000, null)) + "\n";

        var s = ClaudeSessionReader.ParseTail(tail, Now, "abc")!;

        Assert.Equal("알림 개선", s.Title);
        var a = s.Agent!;
        Assert.Equal("claude-opus-5-5", a.Model);
        Assert.Equal(184503, a.ContextTokens);
        Assert.Equal("Bash", a.LastTool);
        Assert.Equal("plan", a.Mode);
        Assert.Equal(56, a.PrNumber);
        Assert.Equal("abc", a.SessionId);
        var json = JsonSerializer.Serialize(s, MobileProtocol.Json);
        Assert.DoesNotContain("SECRET", json);
    }

    [Fact]
    public void The_default_permission_mode_is_not_shown()
    {
        var tail = """{"type":"mode","mode":"default"}""" + "\n" + """{"type":"mode","mode":"plan"}""" + "\n" + """{"cwd":"C:\\x"}""";
        Assert.Null(ClaudeSessionReader.ParseTail("""{"cwd":"C:\\x"}""" + "\n" + """{"type":"mode","mode":"default"}""", Now)!.Agent?.Mode);
        Assert.Equal("plan", ClaudeSessionReader.ParseTail(tail, Now)!.Agent!.Mode);
    }

    [Fact]
    public void Live_status_joins_its_transcript_and_a_quiet_waiting_session_is_still_listed()
    {
        var transcripts = new[]
        {
            new ClaudeSession("작업 A", @"D:\a", T0, new AgentInfo(Model: "claude-opus-5-5", SessionId: "A")),
            new ClaudeSession("작업 B", @"D:\b", T0.AddMinutes(-1), new AgentInfo(SessionId: "B")),
        };
        var live = new[]
        {
            new ClaudeLiveSession("A", "A name", @"D:\a", AgentStatus.Busy, null, T0.AddMinutes(-3), "https://claude.ai/code/session_A"),
            new ClaudeLiveSession("C", "밤새 기다린 세션", @"D:\c", AgentStatus.Waiting, "질문", T0.AddHours(-8), null),
            new ClaudeLiveSession("D", "쉬는 세션", @"D:\d", AgentStatus.Idle, null, T0.AddHours(-8), null),
        };

        var merged = ClaudeSessionReader.Merge(transcripts, live);

        Assert.Equal(new[] { "작업 A", "작업 B", "밤새 기다린 세션" }, merged.Select(m => m.Title));
        var a = merged[0].Agent!;
        Assert.Equal(AgentStatus.Busy, a.Status);
        Assert.Equal("claude-opus-5-5", a.Model);
        Assert.Equal("https://claude.ai/code/session_A", a.RemoteUrl);
        Assert.Null(merged[1].Agent!.Status); // its process ended: no claim either way
        Assert.Equal("질문", merged[2].Agent!.WaitingFor);
        Assert.Equal(AgentStatus.Waiting, merged[2].ToWork().Agent!.Status);
    }

    [Fact]
    public void Waiting_sessions_lead_the_work_tab()
    {
        var items = WorkCatalog.Build(new[]
        {
            new WorkItem(WorkKind.Agent, "Codex", "new", LastActivity: T0),
            new WorkItem(WorkKind.Agent, "Claude Code", "asks", LastActivity: T0.AddHours(-1), Agent: new AgentInfo(AgentStatus.Waiting)),
        }, Array.Empty<SessionWindow>(), _ => null);
        Assert.Equal(new[] { "asks", "new" }, items.Select(i => i.Title));
    }

    // ---- Codex rollouts ------------------------------------------------------------------------

    [Fact]
    public void Codex_lines_fold_into_model_tokens_turn_state_and_last_tool()
    {
        var lines = string.Join('\n',
            """{"timestamp":"2026-11-16T14:00:00.000Z","type":"session_meta","payload":{"id":"x","cwd":"C:\\first"}}""",
            """{"timestamp":"2026-11-16T14:00:01.000Z","type":"world_state","payload":{"state":{"model":"not-this"}}}""",
            """{"timestamp":"2026-11-16T14:00:02.000Z","type":"turn_context","payload":{"cwd":"C:\\proj","model":"gpt-5.5","effort":"high"}}""",
            """{"timestamp":"2026-11-16T14:00:03.000Z","type":"event_msg","payload":{"type":"task_started"}}""",
            """{"timestamp":"2026-11-16T14:00:04.000Z","type":"response_item","payload":{"type":"custom_tool_call","name":"apply_patch","input":"SECRET"}}""",
            """{"timestamp":"2026-11-16T14:00:05.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":51000},"last_token_usage":{"input_tokens":42000},"model_context_window":258400}}}""");

        var d = CodexSessionReader.ParseLines(lines, new CodexDetails());
        var info = d.ToInfo("01a1e375-84c9-7741-a7f3-120940bdace8");

        Assert.Equal(@"C:\proj", d.Cwd);
        Assert.Equal("gpt-5.5", info.Model);
        Assert.Equal("high", info.Effort);
        Assert.Equal(42000, info.ContextTokens);
        Assert.Equal(258400, info.ContextWindow);
        Assert.Equal(51000, info.TotalTokens);
        Assert.Equal("apply_patch", info.LastTool);
        Assert.Equal(AgentStatus.Busy, info.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 14, 0, 3, TimeSpan.Zero), info.StatusSince);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(info, MobileProtocol.Json));

        var done = CodexSessionReader.ParseLines(
            """{"timestamp":"2026-11-16T14:05:00.000Z","type":"event_msg","payload":{"type":"task_complete","last_agent_message":"SECRET"}}""", d);
        Assert.Equal(AgentStatus.Idle, done.ToInfo("x").Status);
        Assert.Equal("gpt-5.5", done.Model); // earlier lines are kept, not re-read
        Assert.Null(new CodexDetails().ToInfo("x").Status);
    }

    [Fact]
    public void A_codex_rollout_is_followed_by_its_appended_lines()
    {
        var home = Path.Combine(Path.GetTempPath(), "hb-codex-f-" + Guid.NewGuid().ToString("N"));
        var day = Directory.CreateDirectory(Path.Combine(home, "sessions", "2026", "11", "16")).FullName;
        var file = Path.Combine(day, "rollout-2026-11-16T14-00-00-01a1e375-84c9-7741-a7f3-120940bdace8.jsonl");
        try
        {
            File.WriteAllText(file,
                """{"type":"turn_context","payload":{"cwd":"C:\\proj","model":"gpt-5.5"}}""" + "\n" +
                """{"timestamp":"2026-11-16T14:00:03.000Z","type":"event_msg","payload":{"type":"task_started"}}""" + "\n" +
                """{"type":"event_msg","payload":{"type":"task_compl""");  // a line still being written
            var reader = new CodexSessionReader(home);
            var first = Assert.Single(reader.ReadActive(DateTime.UtcNow)).Agent!;
            Assert.Equal(AgentStatus.Busy, first.Status);
            Assert.Equal("gpt-5.5", first.Model);
            Assert.Equal("01a1e375-84c9-7741-a7f3-120940bdace8", first.SessionId);

            File.AppendAllText(file, """ete"}}""" + "\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));
            var second = Assert.Single(reader.ReadActive(DateTime.UtcNow)).Agent!;
            Assert.Equal(AgentStatus.Idle, second.Status);
            Assert.Equal("gpt-5.5", second.Model);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    // ---- Alerts --------------------------------------------------------------------------------

    private static AgentState State(string status, DateTimeOffset since, string? waitingFor = null) =>
        new("claude:A", "Claude Code", "알림 개선", @"D:\Project\homebase", status, waitingFor, since);

    [Fact]
    public void The_first_view_only_records_and_waiting_alerts_once()
    {
        var d = new AgentAlertDetector();
        Assert.Empty(d.Update(new[] { State(AgentStatus.Waiting, T0, "승인") }, T0)); // already waiting at start
        Assert.Empty(d.Update(new[] { State(AgentStatus.Busy, T0.AddSeconds(5)) }, T0.AddSeconds(5)));
        var alert = Assert.Single(d.Update(new[] { State(AgentStatus.Waiting, T0.AddSeconds(9), "Bash 실행 승인") }, T0.AddSeconds(9)));
        Assert.Equal(AgentAlertKind.Waiting, alert.Kind);
        Assert.Equal("Bash 실행 승인", alert.Detail);
        Assert.Empty(d.Update(new[] { State(AgentStatus.Waiting, T0.AddSeconds(9), "Bash 실행 승인") }, T0.AddSeconds(12)));
    }

    [Fact]
    public void Done_needs_a_turn_of_a_minute_or_more()
    {
        var d = new AgentAlertDetector();
        d.Update(new[] { State(AgentStatus.Idle, T0) }, T0);
        d.Update(new[] { State(AgentStatus.Busy, T0.AddSeconds(10)) }, T0.AddSeconds(10));
        Assert.Empty(d.Update(new[] { State(AgentStatus.Idle, T0.AddSeconds(40)) }, T0.AddSeconds(40))); // 30 s: watched answer

        d.Update(new[] { State(AgentStatus.Busy, T0.AddMinutes(1)) }, T0.AddMinutes(1));
        // Waiting in the middle alerts on its own; the turn is then timed from when work resumes.
        Assert.Single(d.Update(new[] { State(AgentStatus.Waiting, T0.AddMinutes(2)) }, T0.AddMinutes(2)));
        d.Update(new[] { State(AgentStatus.Busy, T0.AddMinutes(3)) }, T0.AddMinutes(3));
        Assert.Empty(d.Update(new[] { State(AgentStatus.Busy, T0.AddMinutes(3)) }, T0.AddMinutes(6)));
        var done = Assert.Single(d.Update(new[] { State(AgentStatus.Idle, T0.AddMinutes(7)) }, T0.AddMinutes(7)));
        Assert.Equal(AgentAlertKind.Done, done.Kind);
        Assert.Equal("4분", done.Detail);
    }

    [Fact]
    public void A_session_that_disappears_is_forgotten()
    {
        var d = new AgentAlertDetector();
        d.Update(new[] { State(AgentStatus.Busy, T0) }, T0);
        d.Update(Array.Empty<AgentState>(), T0.AddMinutes(5));
        Assert.Empty(d.Update(new[] { State(AgentStatus.Waiting, T0.AddMinutes(6)) }, T0.AddMinutes(6)));
    }

    [Theory]
    [InlineData(30, "1분 미만")]
    [InlineData(150, "2분")]
    [InlineData(3 * 3600 + 5 * 60, "3시간 5분")]
    public void Durations_read_in_korean(int seconds, string expected) =>
        Assert.Equal(expected, AgentAlertDetector.Minutes(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void The_agent_checks_trims_and_rate_limits_alerts()
    {
        Assert.Null(AgentAlertGate.Sanitize(new AgentAlert("boot", "Claude Code", "x", null, null)));
        Assert.Null(AgentAlertGate.Sanitize(new AgentAlert(AgentAlertKind.Done, " ", "x", null, null)));
        var big = AgentAlertGate.Sanitize(new AgentAlert(AgentAlertKind.Done, "Claude Code", new string('가', 500) + "\n2", null, "x\r\ny"))!;
        Assert.Equal(200, big.Title.Length);
        Assert.Equal("x y", big.Detail);

        var gate = new AgentAlertGate();
        var a = new AgentAlert(AgentAlertKind.Waiting, "Claude Code", "알림 개선", @"D:\p", "승인");
        Assert.True(gate.TryPass(a, T0));
        Assert.False(gate.TryPass(a, T0.AddSeconds(30)));
        Assert.True(gate.TryPass(a with { Kind = AgentAlertKind.Done }, T0.AddSeconds(30)));
        Assert.True(gate.TryPass(a, T0.AddSeconds(61)));

        var many = new AgentAlertGate();
        var passed = Enumerable.Range(0, 100).Count(i => many.TryPass(a with { Title = $"s{i}" }, T0.AddSeconds(i)));
        Assert.Equal(AgentAlertGate.DailyLimit, passed);
        Assert.True(many.TryPass(a with { Title = "tomorrow" }, T0.AddDays(1)));
    }

    [Fact]
    public void An_alert_becomes_an_encrypted_push_named_after_the_app()
    {
        var n = AgentAlertGate.ToNotification(
            new AgentAlert(AgentAlertKind.Waiting, "Claude Code", "알림 개선", @"D:\Project\homebase\", "Bash 실행 승인"), T0);
        Assert.Equal(AgentAlertKind.Waiting, n.Category);
        Assert.Equal(OutboundPriority.Warning, n.Priority);
        Assert.Equal(DeliveryPolicy.EncryptedOnly, n.Policy);
        Assert.Equal("알림 개선", n.Title);
        Assert.Equal("입력을 기다립니다: Bash 실행 승인\nhomebase", n.Body);
        Assert.Equal("Claude Code", n.Metadata!["sourceApp"]);

        var done = AgentAlertGate.ToNotification(new AgentAlert(AgentAlertKind.Done, "Codex", "리팩터링", null, "12분"), T0);
        Assert.Equal(OutboundPriority.Info, done.Priority);
        Assert.Equal("작업을 마쳤습니다 (12분 걸림)", done.Body);
    }

    [Fact]
    public void The_bridge_accepts_agent_alerts_from_the_tray() =>
        Assert.Contains(MobileBridge.OpAgentAlert, MobileBridge.AllowedTrayOps);
}
