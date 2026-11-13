using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using LocalOpsBot.Core.Updates;

namespace LocalOpsBot.Tray;

/// <summary>What the update dialog ended with. <see cref="AutoUpdate"/> = the countdown ran out.</summary>
internal enum UpdateDialogChoice { Later, Skip, Update, AutoUpdate }

/// <summary>
/// The "new version available" dialog: the release's CHANGELOG section plus the three answers —
/// update now, skip this version, or later. Closing the window (X / Esc) counts as Later, so the
/// only way to silence a version is the explicit Skip button. When opened with a countdown, the
/// Update button counts down and the dialog answers <see cref="UpdateDialogChoice.AutoUpdate"/>
/// on its own at zero; any button or closing the window ends the countdown first.
/// </summary>
public partial class UpdateDialog : ThemedWindow
{
    private DispatcherTimer? _countdown;
    private int _secondsLeft;

    private UpdateDialog() => InitializeComponent();

    internal UpdateDialogChoice Choice { get; private set; } = UpdateDialogChoice.Later;

    /// <summary>
    /// Shows the dialog modally and returns the user's choice. <paramref name="autoUpdateSeconds"/>
    /// &gt; 0 starts the auto-install countdown on the Update button; 0 waits for a click. Must run
    /// on the UI thread.
    /// </summary>
    internal static UpdateDialogChoice Show(UpdateInfo info, string currentVersion, int autoUpdateSeconds = 0)
    {
        var dlg = new UpdateDialog();
        dlg.TitleText.Text = L.UpdDlgTitle(info.Version.ToString(3));
        dlg.MetaText.Text = L.UpdDlgMeta(currentVersion, info.PublishedAt.ToLocalTime());
        dlg.NotesViewer.Document = BuildDocument(ReleaseNotes.Parse(ReleaseNotes.ExtractChangelog(info.ReleaseNotes)));
        if (autoUpdateSeconds > 0) dlg.StartCountdown(autoUpdateSeconds);
        // The startup check opens this unprompted; bring it forward once without pinning it on top.
        dlg.Loaded += (_, _) => dlg.Activate();
        dlg.ShowDialog();
        return dlg.Choice;
    }

    private void StartCountdown(int seconds)
    {
        _secondsLeft = seconds;
        UpdateButton.Content = L.UpdDlgUpdateCountdown(_secondsLeft);
        UpdateButton.ToolTip = L.UpdDlgCountdownTip;
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += OnCountdownTick;
        _countdown.Start();
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        if (--_secondsLeft > 0)
        {
            UpdateButton.Content = L.UpdDlgUpdateCountdown(_secondsLeft);
            return;
        }
        Answer(UpdateDialogChoice.AutoUpdate);
    }

    private void StopCountdown()
    {
        if (_countdown is null) return;
        _countdown.Stop();
        _countdown.Tick -= OnCountdownTick;
        _countdown = null;
    }

    private void Answer(UpdateDialogChoice choice)
    {
        StopCountdown();
        Choice = choice;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // However the window closes (X, Esc), a tick must never answer for a closed dialog.
        StopCountdown();
        base.OnClosed(e);
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
            doc.Blocks.Add(new Paragraph(new Run(L.UpdDlgNoNotes)));
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

    private void Update_Click(object sender, RoutedEventArgs e) => Answer(UpdateDialogChoice.Update);

    private void Skip_Click(object sender, RoutedEventArgs e) => Answer(UpdateDialogChoice.Skip);

    private void Later_Click(object sender, RoutedEventArgs e) => Answer(UpdateDialogChoice.Later);
}
