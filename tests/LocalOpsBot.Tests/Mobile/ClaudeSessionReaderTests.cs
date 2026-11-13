using LocalOpsBot.Core.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

public class ClaudeSessionReaderTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Custom_title_wins_over_ai_title_and_message_text_is_not_returned()
    {
        var tail = "{\"partial line\n" +
                   "{\"type\":\"ai-title\",\"aiTitle\":\"AI title\"}\n" +
                   "{\"type\":\"user\",\"cwd\":\"D:\\\\Project\\\\homebase\",\"message\":\"SECRET PROMPT\"}\n" +
                   "{\"type\":\"custom-title\",\"customTitle\":\"My task\"}\n";
        var s = ClaudeSessionReader.ParseTail(tail, Now);

        Assert.NotNull(s);
        Assert.Equal("My task", s!.Title);
        Assert.Equal(@"D:\Project\homebase", s.Project);
        Assert.DoesNotContain("SECRET", s.ToString());
    }

    [Fact]
    public void Falls_back_to_ai_title_then_placeholder()
    {
        Assert.Equal("AI only", ClaudeSessionReader.ParseTail("{\"aiTitle\":\"AI only\"}\n", Now)!.Title);
        Assert.Equal("(제목 없음)", ClaudeSessionReader.ParseTail("{\"cwd\":\"C:\\\\x\"}\n", Now)!.Title);
        Assert.Null(ClaudeSessionReader.ParseTail("{\"type\":\"assistant\"}\n", Now));
    }

    [Fact]
    public void Only_recently_written_transcripts_are_reported()
    {
        var root = Path.Combine(Path.GetTempPath(), "hb-claude-" + Guid.NewGuid().ToString("N"));
        var proj = Directory.CreateDirectory(Path.Combine(root, "D--Project-x")).FullName;
        try
        {
            var fresh = Path.Combine(proj, "a.jsonl");
            var old = Path.Combine(proj, "b.jsonl");
            File.WriteAllText(fresh, "{\"customTitle\":\"fresh\"}\n");
            File.WriteAllText(old, "{\"customTitle\":\"old\"}\n");
            File.SetLastWriteTimeUtc(fresh, Now.AddMinutes(-1));
            File.SetLastWriteTimeUtc(old, Now.AddHours(-1));

            var sessions = new ClaudeSessionReader(root).ReadActive(Now);
            Assert.Equal(new[] { "fresh" }, sessions.Select(s => s.Title));
        }
        finally { Directory.Delete(root, true); }
    }
}
