using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using LocalOpsBot.Core.Updates;

namespace LocalOpsBot.Tray;

internal enum UpdateDialogChoice { Later, Skip, Update }

/// <summary>
/// The "new version available" dialog: the release's CHANGELOG section plus the three answers —
/// update now, skip this version, or later. Closing the window (X / Esc) counts as Later, so the
/// only way to silence a version is the explicit Skip button.
/// </summary>
public partial class UpdateDialog : ThemedWindow
{
    private UpdateDialog() => InitializeComponent();

    internal UpdateDialogChoice Choice { get; private set; } = UpdateDialogChoice.Later;

    /// <summary>Shows the dialog modally and returns the user's choice. Must run on the UI thread.</summary>
    internal static UpdateDialogChoice Show(UpdateInfo info, string currentVersion)
    {
        var dlg = new UpdateDialog();
        dlg.TitleText.Text = $"UPDATE AVAILABLE — V{info.Version.ToString(3)}";
        dlg.MetaText.Text = $"Installed v{currentVersion}  ·  Released {info.PublishedAt.ToLocalTime():yyyy-MM-dd}";
        dlg.NotesViewer.Document = BuildDocument(ReleaseNotes.Parse(ReleaseNotes.ExtractChangelog(info.ReleaseNotes)));
        // The startup check opens this unprompted; bring it forward once without pinning it on top.
        dlg.Loaded += (_, _) => dlg.Activate();
        dlg.ShowDialog();
        return dlg.Choice;
    }

    private static FlowDocument BuildDocument(IReadOnlyList<ReleaseNoteBlock> blocks)
    {
        var ink = (Brush)Application.Current.FindResource("Brush.Ink");
        var accent = (Brush)Application.Current.FindResource("Brush.ChromeIndigo");
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(6),
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Sans"),
            FontSize = 12,
            Foreground = ink,
            // FlowDocument justifies by default, which stretches the gaps in short wrapped lines.
            TextAlignment = TextAlignment.Left,
        };

        if (blocks.Count == 0)
        {
            doc.Blocks.Add(new Paragraph(new Run("No release notes were published for this version.")));
            return doc;
        }

        foreach (var block in blocks)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            switch (block.Kind)
            {
                case ReleaseNoteBlockKind.Heading:
                    p.FontSize = 15;
                    p.FontWeight = FontWeights.Bold;
                    p.Margin = new Thickness(0, 4, 0, 8);
                    break;
                case ReleaseNoteBlockKind.Subheading:
                    p.FontSize = 13;
                    p.FontWeight = FontWeights.Bold;
                    p.Foreground = accent;
                    p.Margin = new Thickness(0, 6, 0, 4);
                    break;
                case ReleaseNoteBlockKind.Bullet:
                    p.Margin = new Thickness(14, 0, 0, 4);
                    p.TextIndent = -10;
                    p.Inlines.Add(new Run("• ") { Foreground = accent });
                    break;
            }
            AddInlines(p.Inlines, block.Text);
            doc.Blocks.Add(p);
        }
        return doc;
    }

    // Inline subset the CHANGELOG uses: **bold** (which may contain `code`) and `code`.
    // Anything else is shown verbatim.
    private static void AddInlines(InlineCollection target, string text)
    {
        foreach (var part in Regex.Split(text, @"(\*\*.+?\*\*)"))
        {
            if (part.Length == 0) continue;
            if (part.Length > 4 && part.StartsWith("**") && part.EndsWith("**"))
            {
                var bold = new Bold();
                AddCodeAndText(bold.Inlines, part[2..^2]);
                target.Add(bold);
            }
            else
            {
                AddCodeAndText(target, part);
            }
        }
    }

    private static void AddCodeAndText(InlineCollection target, string text)
    {
        foreach (var part in Regex.Split(text, @"(`[^`]+`)"))
        {
            if (part.Length == 0) continue;
            if (part.Length > 2 && part.StartsWith('`') && part.EndsWith('`'))
                target.Add(new Run(part[1..^1])
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = (Brush)Application.Current.FindResource("Brush.Platinum"),
                });
            else
                target.Add(new Run(part));
        }
    }

    private void Update_Click(object sender, RoutedEventArgs e) { Choice = UpdateDialogChoice.Update; Close(); }

    private void Skip_Click(object sender, RoutedEventArgs e) { Choice = UpdateDialogChoice.Skip; Close(); }

    private void Later_Click(object sender, RoutedEventArgs e) { Choice = UpdateDialogChoice.Later; Close(); }
}
