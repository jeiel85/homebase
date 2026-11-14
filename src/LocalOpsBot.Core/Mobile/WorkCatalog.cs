namespace LocalOpsBot.Core.Mobile;

/// <summary>Values of <see cref="WorkItem.Kind"/> (DECISIONS M-26).</summary>
public static class WorkKind
{
    /// <summary>An AI coding session (Claude Code, Codex), read from its transcript folder.</summary>
    public const string Agent = "agent";

    /// <summary>A browser window; the title is its active tab.</summary>
    public const string Browser = "browser";

    /// <summary>An open document (Office, Hangul, PDF, design files).</summary>
    public const string Document = "document";

    /// <summary>A code editor or IDE window.</summary>
    public const string Editor = "editor";
}

/// <summary>
/// One entry of the phone's work tab (DECISIONS M-26). <c>Detail</c> is the project folder for
/// agents and editors. <c>LastActivity</c> is set for agents (transcript write time).
/// </summary>
public sealed record WorkItem(
    string Kind,
    string App,
    string Title,
    string? Detail = null,
    DateTimeOffset? LastActivity = null,
    bool Foreground = false);

/// <summary>
/// Turns the user's top-level windows into work items: which processes count as a browser,
/// document or editor, and which window-title suffix names the app (" - Google Chrome").
/// Pure so the table is pinned by tests.
/// </summary>
public static class WorkCatalog
{
    public const int MaxItems = 30;

    private static readonly string ZeroWidthSpace = ((char)0x200B).ToString();
    private const char NoBreakSpace = (char)0x00A0;

    private sealed record Entry(string Kind, string App, string[] Suffixes, bool ProjectAfterFile = false);

    // Process name (no ".exe") → how its windows read. Suffixes are tried longest first.
    private static readonly Dictionary<string, Entry> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = new(WorkKind.Browser, "Chrome", [" - Google Chrome"]),
        ["msedge"] = new(WorkKind.Browser, "Edge", [" - Microsoft Edge"]),
        ["firefox"] = new(WorkKind.Browser, "Firefox", [" — Mozilla Firefox", " - Mozilla Firefox"]),
        ["whale"] = new(WorkKind.Browser, "Whale", [" - Whale"]),
        ["brave"] = new(WorkKind.Browser, "Brave", [" - Brave"]),
        ["opera"] = new(WorkKind.Browser, "Opera", [" - Opera"]),
        ["vivaldi"] = new(WorkKind.Browser, "Vivaldi", [" - Vivaldi"]),
        ["arc"] = new(WorkKind.Browser, "Arc", []),

        ["WINWORD"] = new(WorkKind.Document, "Word", [" - Word"]),
        ["EXCEL"] = new(WorkKind.Document, "Excel", [" - Excel"]),
        ["POWERPNT"] = new(WorkKind.Document, "PowerPoint", [" - PowerPoint"]),
        ["ONENOTE"] = new(WorkKind.Document, "OneNote", [" - OneNote"]),
        ["Hwp"] = new(WorkKind.Document, "한글", [" - 한컴오피스 한글", " - 한글", " - Hwp"]),
        ["Acrobat"] = new(WorkKind.Document, "Acrobat", [" - Adobe Acrobat Reader (64-bit)", " - Adobe Acrobat Reader", " - Adobe Acrobat Pro (64-bit)", " - Adobe Acrobat"]),
        ["AcroRd32"] = new(WorkKind.Document, "Acrobat", [" - Adobe Acrobat Reader DC", " - Adobe Acrobat Reader"]),
        ["Notion"] = new(WorkKind.Document, "Notion", []),
        ["Obsidian"] = new(WorkKind.Document, "Obsidian", []),
        ["Figma"] = new(WorkKind.Document, "Figma", [" – Figma", " - Figma"]),
        ["blender"] = new(WorkKind.Document, "Blender", []),

        ["Code"] = new(WorkKind.Editor, "VS Code", [" - Visual Studio Code"], ProjectAfterFile: true),
        ["Cursor"] = new(WorkKind.Editor, "Cursor", [" - Cursor"], ProjectAfterFile: true),
        ["Windsurf"] = new(WorkKind.Editor, "Windsurf", [" - Windsurf"], ProjectAfterFile: true),
        ["devenv"] = new(WorkKind.Editor, "Visual Studio", [" - Microsoft Visual Studio"]),
        ["idea64"] = new(WorkKind.Editor, "IntelliJ IDEA", []),
        ["pycharm64"] = new(WorkKind.Editor, "PyCharm", []),
        ["rider64"] = new(WorkKind.Editor, "Rider", []),
        ["webstorm64"] = new(WorkKind.Editor, "WebStorm", []),
        ["clion64"] = new(WorkKind.Editor, "CLion", []),
        ["goland64"] = new(WorkKind.Editor, "GoLand", []),
        ["studio64"] = new(WorkKind.Editor, "Android Studio", []),
        ["Unity"] = new(WorkKind.Editor, "Unity", []),
        ["notepad++"] = new(WorkKind.Editor, "Notepad++", [" - Notepad++"]),
        ["sublime_text"] = new(WorkKind.Editor, "Sublime Text", [" - Sublime Text"]),
    };

    private static readonly string[] KindOrder = [WorkKind.Agent, WorkKind.Editor, WorkKind.Document, WorkKind.Browser];

    /// <summary>The work item a window stands for, or <c>null</c> when its process is not in the table.</summary>
    public static WorkItem? FromWindow(string processName, string windowTitle, bool foreground)
    {
        if (!Table.TryGetValue(processName, out var entry)) return null;
        // Edge writes "Microsoft<U+200B> Edge" with a zero-width space; other apps pad with no-break spaces.
        var title = windowTitle.Replace(ZeroWidthSpace, "").Replace(NoBreakSpace, ' ').TrimEnd();
        foreach (var suffix in entry.Suffixes.OrderByDescending(s => s.Length))
        {
            // "Google Chrome" alone (no page) is the suffix without its separator.
            if (string.Equals(title.Trim(), suffix.TrimStart(' ', '-', '—', '–'), StringComparison.OrdinalIgnoreCase))
                return null;
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^suffix.Length];
                break;
            }
        }
        // Unsaved-change markers ("● file", "*file") say nothing about the work.
        title = title.TrimStart('●', '*', ' ').Trim();
        if (title.Length == 0 || string.Equals(title, entry.App, StringComparison.OrdinalIgnoreCase)) return null;

        string? detail = null;
        if (entry.ProjectAfterFile)
        {
            // VS Code family: "file - project" → title = file, detail = project.
            var cut = title.LastIndexOf(" - ", StringComparison.Ordinal);
            if (cut > 0)
            {
                detail = title[(cut + 3)..].Trim();
                title = title[..cut].Trim();
            }
        }
        return new WorkItem(entry.Kind, entry.App, Truncate(title, 160), detail is { Length: > 0 } ? Truncate(detail, 120) : null,
            null, foreground);
    }

    /// <summary>
    /// The whole work list: agent sessions newest first, then window items with the foreground
    /// window first and the rest grouped by kind. Duplicate windows (same app and title) collapse.
    /// </summary>
    public static IReadOnlyList<WorkItem> Build(
        IEnumerable<WorkItem>? agents,
        IEnumerable<SessionWindow> windows,
        Func<int, string?> processNameOf)
    {
        var list = new List<WorkItem>();
        if (agents is not null)
            list.AddRange(agents.OrderByDescending(a => a.LastActivity ?? DateTimeOffset.MinValue));

        var seen = new HashSet<(string, string)>();
        var fromWindows = new List<WorkItem>();
        foreach (var w in windows)
        {
            var name = processNameOf(w.Pid);
            if (name is null) continue;
            var item = FromWindow(name, w.Title, w.Foreground);
            if (item is null || !seen.Add((item.App, item.Title))) continue;
            fromWindows.Add(item);
        }
        list.AddRange(fromWindows
            .OrderByDescending(i => i.Foreground)
            .ThenBy(i => Array.IndexOf(KindOrder, i.Kind)));
        return list.Take(MaxItems).ToList();
    }

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
