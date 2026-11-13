using LocalOpsBot.Core.Updates;
using Xunit;

namespace LocalOpsBot.Tests.Core.Updates;

public sealed class ReleaseNotesTests
{
    private const string Body =
        "## 📥 어느 파일을 받아야 하나요?\r\n\r\n" +
        "| 파일 | 이럴 때 |\r\n|---|---|\r\n\r\n---\r\n\r\n" +
        "## v0.14.0 — Homebase Mobile\r\n\r\n" +
        "### Added\r\n" +
        "- **Android app.** Pair any number of PCs by QR code, pick one\r\n" +
        "  from the list, and watch it **live\r\n  updates**.\r\n" +
        "- Second item\r\n\r\n" +
        "Plain paragraph that\r\nwraps.\r\n";

    [Fact]
    public void ExtractChangelog_DropsDownloadGuideHeader()
    {
        var notes = ReleaseNotes.ExtractChangelog(Body);

        Assert.StartsWith("## v0.14.0", notes);
        Assert.DoesNotContain("어느 파일을", notes);
    }

    [Fact]
    public void ExtractChangelog_KeepsBodyWithoutVersionHeading()
    {
        Assert.Equal("Hand-written notes", ReleaseNotes.ExtractChangelog("  Hand-written notes \n"));
        Assert.Equal("", ReleaseNotes.ExtractChangelog(null));
    }

    [Fact]
    public void Parse_JoinsWrappedBulletLines()
    {
        var blocks = ReleaseNotes.Parse(ReleaseNotes.ExtractChangelog(Body));

        Assert.Collection(blocks,
            b => Assert.Equal(new ReleaseNoteBlock(ReleaseNoteBlockKind.Heading, "v0.14.0 — Homebase Mobile"), b),
            b => Assert.Equal(new ReleaseNoteBlock(ReleaseNoteBlockKind.Subheading, "Added"), b),
            b => Assert.Equal(new ReleaseNoteBlock(ReleaseNoteBlockKind.Bullet,
                "**Android app.** Pair any number of PCs by QR code, pick one from the list, and watch it **live updates**."), b),
            b => Assert.Equal(new ReleaseNoteBlock(ReleaseNoteBlockKind.Bullet, "Second item"), b),
            b => Assert.Equal(new ReleaseNoteBlock(ReleaseNoteBlockKind.Paragraph, "Plain paragraph that wraps."), b));
    }

    [Theory]
    [InlineData("0.14.0", "0.14.0", false)]
    [InlineData("0.14.0", " 0.14.0 ", false)]
    [InlineData("0.14.1", "0.14.0", true)]
    [InlineData("0.14.0", "", true)]
    [InlineData("0.14.0", null, true)]
    [InlineData("0.14.0", "garbage", true)]
    public void ShouldPrompt_SkipsOnlyTheSkippedVersion(string available, string? skipped, bool expected)
    {
        Assert.Equal(expected, ReleaseNotes.ShouldPrompt(Version.Parse(available), skipped));
    }
}
