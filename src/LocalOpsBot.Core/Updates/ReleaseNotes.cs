using System.Text;

namespace LocalOpsBot.Core.Updates;

public enum ReleaseNoteBlockKind { Heading, Subheading, Bullet, Paragraph }

/// <summary>One rendered unit of release notes. <see cref="Text"/> may still hold inline markdown.</summary>
public sealed record ReleaseNoteBlock(ReleaseNoteBlockKind Kind, string Text);

/// <summary>
/// Turns a GitHub release body into what the tray's update dialog shows. Pure text work so it can
/// be tested without WPF; the dialog only maps the blocks to a FlowDocument.
/// </summary>
public static class ReleaseNotes
{
    /// <summary>
    /// The release body starts with the download guide (<c>installer/release-notes-header.md</c>):
    /// which file to grab, the asset table, prerequisites. Someone who is already running Homebase
    /// needs none of that, so keep only the CHANGELOG section — from the first <c>## v</c> heading
    /// on. A body without such a heading (hand-written release) is returned as-is.
    /// </summary>
    public static string ExtractChangelog(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        var lines = Normalize(body).Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("## v", StringComparison.OrdinalIgnoreCase));
        var kept = start >= 0 ? lines[start..] : lines;
        return string.Join('\n', kept).Trim();
    }

    /// <summary>
    /// Parses the small markdown subset the CHANGELOG uses: <c>##</c> / <c>###</c> headings,
    /// <c>-</c>/<c>*</c> bullets, and paragraphs. CHANGELOG bullets are hard-wrapped at ~100 columns
    /// with indented continuation lines, so lines are joined until a blank line or the next block
    /// marker — otherwise each wrapped fragment would render as its own paragraph and inline
    /// <c>**bold**</c> spanning a wrap would break.
    /// </summary>
    public static IReadOnlyList<ReleaseNoteBlock> Parse(string? markdown)
    {
        var blocks = new List<ReleaseNoteBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        ReleaseNoteBlockKind? kind = null;
        var text = new StringBuilder();

        void Flush()
        {
            if (kind is { } k && text.Length > 0)
                blocks.Add(new ReleaseNoteBlock(k, text.ToString().Trim()));
            kind = null;
            text.Clear();
        }

        foreach (var raw in Normalize(markdown).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "---") { Flush(); continue; }

            if (line.StartsWith("### ")) { Flush(); blocks.Add(new(ReleaseNoteBlockKind.Subheading, line[4..].Trim())); continue; }
            if (line.StartsWith("## ")) { Flush(); blocks.Add(new(ReleaseNoteBlockKind.Heading, line[3..].Trim())); continue; }
            if (line.StartsWith("# ")) { Flush(); blocks.Add(new(ReleaseNoteBlockKind.Heading, line[2..].Trim())); continue; }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                Flush();
                kind = ReleaseNoteBlockKind.Bullet;
                text.Append(line[2..].Trim());
                continue;
            }

            // Continuation of the current bullet/paragraph, or the start of a new paragraph.
            kind ??= ReleaseNoteBlockKind.Paragraph;
            if (text.Length > 0) text.Append(' ');
            text.Append(line);
        }

        Flush();
        return blocks;
    }

    /// <summary>
    /// Whether the startup check should pop the dialog: not when the user already chose
    /// "skip this version" for exactly this version. A newer release than the skipped one prompts
    /// again — skipping is per version, not "stop asking".
    /// </summary>
    public static bool ShouldPrompt(Version available, string? skippedVersion) =>
        !IsSameVersion(available, skippedVersion);

    /// <summary>
    /// Whether the startup dialog may count down and install on its own: not when an earlier
    /// countdown already started installing exactly this version — it is still on offer, so that
    /// unattended install didn't take, and repeating it would loop on every launch. A newer
    /// release counts down again.
    /// </summary>
    public static bool ShouldAutoInstall(Version available, string? autoAttemptedVersion) =>
        !IsSameVersion(available, autoAttemptedVersion);

    private static bool IsSameVersion(Version available, string? stored) =>
        Version.TryParse(stored?.Trim(), out var parsed) && parsed == available;

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');
}
