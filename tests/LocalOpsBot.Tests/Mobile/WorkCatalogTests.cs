using LocalOpsBot.Core.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

public class WorkCatalogTests
{
    [Theory]
    [InlineData("chrome", "Release notes - Google Chrome", "browser", "Chrome", "Release notes", null)]
    [InlineData("msedge", "Inbox - Outlook - Personal - Microsoft\u200b Edge", "browser", "Edge", "Inbox - Outlook - Personal", null)]
    [InlineData("firefox", "MDN Web Docs — Mozilla Firefox", "browser", "Firefox", "MDN Web Docs", null)]
    [InlineData("WINWORD", "견적서.docx - Word", "document", "Word", "견적서.docx", null)]
    [InlineData("EXCEL", "매출 2026.xlsx - Excel", "document", "Excel", "매출 2026.xlsx", null)]
    [InlineData("Hwp", "보고서.hwp - 한글", "document", "한글", "보고서.hwp", null)]
    [InlineData("Code", "● Program.cs - homebase - Visual Studio Code", "editor", "VS Code", "Program.cs", "homebase")]
    [InlineData("Cursor", "README.md - app - Cursor", "editor", "Cursor", "README.md", "app")]
    [InlineData("studio64", "homebase – PcDetailScreen.kt [homebase.app.main]", "editor", "Android Studio", "homebase – PcDetailScreen.kt [homebase.app.main]", null)]
    public void Windows_of_known_apps_become_work_items(string process, string title, string kind, string app, string expectTitle, string? detail)
    {
        var item = WorkCatalog.FromWindow(process, title, foreground: false);

        Assert.NotNull(item);
        Assert.Equal(kind, item!.Kind);
        Assert.Equal(app, item.App);
        Assert.Equal(expectTitle, item.Title);
        Assert.Equal(detail, item.Detail);
    }

    [Theory]
    [InlineData("KakaoTalk", "카카오톡")]          // a messenger, not work
    [InlineData("chrome", "Google Chrome")]       // nothing but the app name
    [InlineData("WINWORD", " - Word")]            // empty after the suffix
    public void Other_windows_are_not_work(string process, string title) =>
        Assert.Null(WorkCatalog.FromWindow(process, title, false));

    [Fact]
    public void Agents_come_first_then_the_foreground_window_then_by_kind_without_duplicates()
    {
        var at = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
        var agents = new[]
        {
            new WorkItem(WorkKind.Agent, "Codex", "older", LastActivity: at),
            new WorkItem(WorkKind.Agent, "Claude Code", "newer", LastActivity: at.AddMinutes(1)),
        };
        var windows = new[]
        {
            new SessionWindow(1, 10, "Docs - Google Chrome", false),
            new SessionWindow(2, 10, "Docs - Google Chrome", false), // same tab title in a second window
            new SessionWindow(3, 20, "a.docx - Word", true),
            new SessionWindow(4, 30, "x.cs - proj - Visual Studio Code", false),
            new SessionWindow(5, 40, "카카오톡", false),
        };
        var names = new Dictionary<int, string> { [10] = "chrome", [20] = "WINWORD", [30] = "Code", [40] = "KakaoTalk" };

        var work = WorkCatalog.Build(agents, windows, pid => names.GetValueOrDefault(pid));

        Assert.Equal(new[] { "newer", "older", "a.docx", "x.cs", "Docs" }, work.Select(w => w.Title));
        Assert.True(work[2].Foreground);
    }
}

public class CodexSessionReaderTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "hb-codex-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_newest_cwd_in_the_tail_wins_and_prompt_lines_are_ignored()
    {
        var tail = string.Join('\n',
            "partial line from the middle of the file",
            """{"type":"session_meta","payload":{"id":"x","cwd":"C:\\old"}}""",
            """{"type":"response_item","payload":{"text":"\"cwd\": \"C:\\fake\""}}""",
            """{"type":"turn_context","payload":{"cwd":"C:\\proj"}}""",
            """{"type":"event_msg","payload":{"type":"task_complete"}}""");

        Assert.Equal(@"C:\proj", CodexSessionReader.ParseCwd(tail));
    }

    [Fact]
    public void A_renamed_thread_keeps_its_latest_name()
    {
        var map = CodexSessionReader.ParseIndex(string.Join('\n',
            """{"id":"a","thread_name":"first","updated_at":"2026-11-13T08:00:00Z"}""",
            """{"id":"b","thread_name":"other"}""",
            """{"id":"a","thread_name":"renamed"}""",
            "not json"));

        Assert.Equal("renamed", map["a"]);
        Assert.Equal("other", map["b"]);
    }

    [Fact]
    public void Only_sessions_written_in_the_last_ten_minutes_are_listed_with_their_title_and_folder()
    {
        var now = DateTime.UtcNow;
        var day = Path.Combine(_home, "sessions", "2026", "11", "14");
        Directory.CreateDirectory(day);
        const string active = "01a1d3f0-159e-75c2-b477-c495094e3144";
        const string idle = "01a1bb4a-a563-7ed1-8e65-ed229e7b755d";
        var activeFile = Path.Combine(day, $"rollout-2026-11-14T09-00-00-{active}.jsonl");
        File.WriteAllText(activeFile, """{"type":"turn_context","payload":{"cwd":"D:\\Project\\homebase"}}""" + "\n");
        var idleFile = Path.Combine(day, $"rollout-2026-11-14T07-00-00-{idle}.jsonl");
        File.WriteAllText(idleFile, """{"type":"turn_context","payload":{"cwd":"D:\\x"}}""" + "\n");
        File.SetLastWriteTimeUtc(idleFile, now - TimeSpan.FromMinutes(30));
        File.WriteAllText(Path.Combine(_home, "session_index.jsonl"),
            $$"""{"id":"{{active}}","thread_name":"Homebase 다중 기기"}""" + "\n");

        var items = new CodexSessionReader(_home).ReadActive(now);

        var item = Assert.Single(items);
        Assert.Equal(WorkKind.Agent, item.Kind);
        Assert.Equal("Codex", item.App);
        Assert.Equal("Homebase 다중 기기", item.Title);
        Assert.Equal(@"D:\Project\homebase", item.Detail);
    }

    [Fact]
    public void A_missing_codex_folder_is_simply_empty() =>
        Assert.Empty(new CodexSessionReader(Path.Combine(_home, "nope")).ReadActive(DateTime.UtcNow));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }
}
