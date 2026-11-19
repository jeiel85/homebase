using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Tray.Dashboard;
using LocalOpsBot.Tray.Services;
using LocalOpsBot.Tray.Services.Mobile;

namespace LocalOpsBot.Tray;

/// <summary>
/// The PC's notification center (DECISIONS M-32), the dashboard's Notifications tab (M-49):
/// notifications other devices relayed here and this PC's alerts, newest first, grouped by day.
/// Sources are down the left; a row expands to its full text. Coming into view marks everything
/// shown as read; new arrivals while it is in view are added at the top.
/// Senders show under the group's alias for them (M-51); the history keeps the name they sent, so
/// renaming a device renames its past notifications too.
/// </summary>
public partial class NotificationCenterPanel : UserControl
{
    private const int PageSize = 50;
    private const string AlertFilter = "\u0001alert";
    // Files that came in and went out, apart from everything else (M-64, M-66).
    private const string ReceivedFilter = "\u0001files-received";
    private const string SentFilter = "\u0001files-sent";
    // Remote commands phones asked this PC for (M-69).
    private const string CommandsFilter = "\u0001commands";

    private readonly List<HistoryEntry> _entries = new();
    private DateTimeOffset _seenAtOpen;
    private bool _more;
    private bool _loading;
    private bool _inView;
    private bool _newWhileLoading;
    private string? _filter; // null = all, AlertFilter, or a device name (as sent, not its alias)
    private readonly Action _onAliases;

    /// <summary>
    /// The tab is in front of a shown window: this page adds new notifications and marks them read
    /// itself, so the tray does not count them as unread.
    /// </summary>
    internal bool InView => _inView;

    public NotificationCenterPanel()
    {
        InitializeComponent();
        MobileHost.ToastRequested += OnToastRequested;
        // A device renamed here, on a phone or on another PC (M-51) shows under its new name at once.
        _onAliases = () => Dispatcher.BeginInvoke(() => { if (_entries.Count > 0) Render(); });
        DeviceNames.Aliases.Changed += _onAliases;
    }

    /// <summary>
    /// The dashboard calls this when the tab comes into view (picked, or the window shown or restored)
    /// and when it leaves. Each time it comes into view it reads the list again, and what was new
    /// since the last look gets the unread dot — as opening the window used to.
    /// </summary>
    internal async void SetInView(bool inView)
    {
        if (inView == _inView) return;
        _inView = inView;
        if (!inView) return;
        _seenAtOpen = NotificationCenter.LastSeen();
        await ReloadAsync();
    }

    /// <summary>The tray is shutting down.</summary>
    internal void Stop()
    {
        MobileHost.ToastRequested -= OnToastRequested;
        DeviceNames.Aliases.Changed -= _onAliases;
    }

    private void OnToastRequested(TrayToast _) =>
        // The Agent writes the history before it sends the balloon, so the new entry is already there.
        // Out of view the tray counts it as unread instead.
        Dispatcher.BeginInvoke(new Action(async () => { if (_inView) await AddNewestAsync(); }));

    private async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var (page, failure) = await NotificationCenter.ListAsync(null, PageSize);
            if (page is null)
            {
                ShowMessage(failure switch
                {
                    NotificationCenter.Failure.FeatureOff => L.CenterFeatureOff,
                    NotificationCenter.Failure.NoAgent => L.CenterNoAgent,
                    _ => L.CenterFailed,
                });
                return;
            }
            _entries.Clear();
            _entries.AddRange(page.Entries);
            _more = page.More;
            // A source picked last time that is no longer in the list would leave nothing picked.
            if (_filter is not null && !_entries.Any(Matches)) _filter = null;
            // Left the tab while the list was read: nothing was looked at, so nothing becomes read
            // (the list is read again when the tab comes back).
            if (_inView) MarkShownAsSeen();
            Render();
        }
        finally { _loading = false; }
        // One arrived while the list was being read; the read may have missed it.
        if (_newWhileLoading) await AddNewestAsync();
    }

    private async Task AddNewestAsync()
    {
        if (_loading)
        {
            _newWhileLoading = true;
            return;
        }
        _newWhileLoading = false;
        // The tray did not count it because this tab was in view when it arrived; if the tab has been
        // left since (before or during the read), count it as unread instead of showing it.
        if (!_inView)
        {
            await NotificationCenter.RefreshUnreadAsync();
            return;
        }
        var (page, _) = await NotificationCenter.ListAsync(null, PageSize);
        if (!_inView)
        {
            await NotificationCenter.RefreshUnreadAsync();
            return;
        }
        if (page is null) return;
        var newest = _entries.Count > 0 ? _entries[0].At : DateTimeOffset.MinValue;
        var fresh = page.Entries.Where(e => e.At > newest).ToList();
        if (fresh.Count == 0) return;
        _entries.InsertRange(0, fresh);
        if (_entries.Count == fresh.Count) _more = page.More;
        MarkShownAsSeen();
        Render();
    }

    private async void More_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _entries.Count == 0) return;
        _loading = true;
        MoreButton.IsEnabled = false;
        try
        {
            var (page, _) = await NotificationCenter.ListAsync(HistoryCursor.After(_entries[^1]), PageSize);
            if (page is null) return;
            _entries.AddRange(page.Entries);
            _more = page.More;
            Render();
        }
        finally
        {
            _loading = false;
            MoreButton.IsEnabled = true;
        }
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Show(L.CenterClearTitle, L.CenterClearMessage, L.CenterClearConfirm, L.Cancel, owner: Window.GetWindow(this))) return;
        if (!await NotificationCenter.ClearAsync())
        {
            MessageDialog.Show(L.CenterClearTitle, L.CenterFailed, owner: Window.GetWindow(this));
            return;
        }
        if (_filter is not null && _filter != AlertFilter) _filter = null;
        await ReloadAsync();
    }

    private void MarkShownAsSeen() =>
        NotificationCenter.MarkSeen(_entries.Count > 0 ? _entries[0].At : DateTimeOffset.UtcNow);

    // ── Rendering ────────────────────────────────────────────────────────────

    private void Render()
    {
        RenderFilters();
        ListPanel.Children.Clear();

        var shown = _entries.Where(Matches).ToList();
        ClearButton.IsEnabled = _entries.Any(e => e.Kind == HistoryKind.Device);
        MoreButton.Visibility = _more ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = L.CenterCount(shown.Count, _more);

        if (_entries.Count == 0)
        {
            ShowMessage(ForwardingOn() ? L.CenterEmpty : L.CenterEmptyForwardingOff, keepFooter: true);
            return;
        }
        if (shown.Count == 0)
        {
            ShowMessage(L.CenterNoMatch, keepFooter: true);
            return;
        }

        foreach (var day in shown.GroupBy(e => e.At.ToLocalTime().Date))
        {
            ListPanel.Children.Add(new Border
            {
                Style = Res<Style>("SectionHeaderBar"),
                Child = new TextBlock { Text = DayLabel(day.Key), Style = Res<Style>("Type.SectionLabel") },
            });
            var rows = new StackPanel();
            var first = true;
            foreach (var entry in day)
            {
                if (!first)
                    rows.Children.Add(new Border { Height = 1, Background = Res<Brush>("Brush.Platinum"), Margin = new Thickness(0, 6, 0, 6) });
                rows.Children.Add(Row(entry));
                first = false;
            }
            ListPanel.Children.Add(new Border { Style = Res<Style>("Card"), Margin = new Thickness(0, 0, 0, 12), Child = rows });
        }
    }

    // Source tiles, drawn like the device list's: everything, this PC's alerts, then each device that
    // sent something (with its PC or phone outline). Counts are of what is loaded; the total gets a
    // "+" while older ones remain (which source they are from is not known until they are loaded).
    private void RenderFilters()
    {
        FilterPanel.Children.Clear();
        FilterPanel.Children.Add(FilterTile(L.CenterAll, null, _entries.Count, pc: null));
        var alerts = _entries.Count(e => e.Kind == HistoryKind.Alert);
        if (alerts > 0) FilterPanel.Children.Add(FilterTile(L.CenterAlerts, AlertFilter, alerts, pc: null));

        var received = _entries.Count(e => Matches(e, ReceivedFilter));
        var sent = _entries.Count(e => Matches(e, SentFilter));
        if (received + sent > 0)
        {
            var files = Hb.Label(L.CenterFilesHead, Hb.InkSoft, 11);
            files.Margin = new Thickness(6, 12, 0, 6);
            FilterPanel.Children.Add(files);
            if (received > 0) FilterPanel.Children.Add(FilterTile(L.CenterFilesReceived, ReceivedFilter, received, pc: null));
            if (sent > 0) FilterPanel.Children.Add(FilterTile(L.CenterFilesSent, SentFilter, sent, pc: null));
        }
        var commands = _entries.Count(e => Matches(e, CommandsFilter));
        if (commands > 0) FilterPanel.Children.Add(FilterTile(L.CenterCommands, CommandsFilter, commands, pc: null));

        var sources = _entries.Where(e => e.Kind == HistoryKind.Device && e.Source is not null)
            .GroupBy(e => e.Source!, StringComparer.Ordinal)
            .Select(g => (Group: g, Label: DeviceNames.Show(g.First().SourceKind, g.Key)))
            .OrderBy(x => x.Label, StringComparer.CurrentCulture)
            .ToList();
        if (sources.Count == 0) return;
        var head = Hb.Label(L.CenterFrom, Hb.InkSoft, 11);
        head.Margin = new Thickness(6, 12, 0, 6);
        FilterPanel.Children.Add(head);
        // The group's alias on the tile; the filter keeps the name the device sent (M-51).
        foreach (var (g, label) in sources)
            FilterPanel.Children.Add(FilterTile(label, g.Key, g.Count(), pc: g.First().SourceKind == DeviceKind.Pc));
    }

    private Border FilterTile(string label, string? key, int count, bool? pc)
    {
        var on = _filter == key;
        var g = Hb.Columns(Hb.Px(pc is null ? 0 : 30), Hb.Star(), Hb.Auto);
        if (pc is { } isPc) g.Children.Add(Hb.At(Hb.Glyph(isPc, on ? Hb.OnSlab : Hb.Ink, 18), 0));
        var name = Hb.Text(label, 13, on ? Hb.OnSlab : Hb.Ink, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(Hb.At(name, 1));
        var more = key is null && _more ? "+" : "";
        var n = Hb.Text(count.ToString(CultureInfo.CurrentCulture) + more, 11.5, on ? Hb.OnSlabSoft : Hb.InkSoft);
        n.VerticalAlignment = VerticalAlignment.Center;
        n.Margin = new Thickness(8, 0, 0, 0);
        g.Children.Add(Hb.At(n, 2));

        var tile = new Border
        {
            Background = on ? Hb.Slab : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            Child = g,
            // Reachable with Tab and Enter/Space, and named for screen readers, as the chips were.
            Focusable = true,
            FocusVisualStyle = Res<Style>("RailFocus"),
        };
        System.Windows.Automation.AutomationProperties.SetName(tile, label);
        if (!on)
        {
            tile.MouseEnter += (_, _) => tile.Background = Hb.Plate;
            tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        }
        tile.MouseLeftButtonUp += (_, _) => Pick(key);
        tile.KeyDown += (_, args) =>
        {
            if (args.Key is not (Key.Enter or Key.Space)) return;
            args.Handled = true;
            Pick(key);
        };
        return tile;
    }

    private void Pick(string? key)
    {
        if (_filter == key) return;
        _filter = key;
        Render();
        ListScroll.ScrollToTop();
        // The tiles were rebuilt: keep the keyboard on the one just picked.
        FilterPanel.Children.OfType<Border>()
            .FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == LabelFor(key))?.Focus();
    }

    private string LabelFor(string? key) => key switch
    {
        null => L.CenterAll,
        AlertFilter => L.CenterAlerts,
        ReceivedFilter => L.CenterFilesReceived,
        SentFilter => L.CenterFilesSent,
        CommandsFilter => L.CenterCommands,
        _ => key,
    };

    private bool Matches(HistoryEntry e) => Matches(e, _filter);

    private static bool Matches(HistoryEntry e, string? filter) => filter switch
    {
        null => true,
        AlertFilter => e.Kind == HistoryKind.Alert,
        ReceivedFilter => e.Kind == HistoryKind.Device && e.App == TransferHistory.Received,
        SentFilter => e.Kind == HistoryKind.Device && e.App == TransferHistory.Sent,
        CommandsFilter => e.Kind == HistoryKind.Device && e.App == RemoteCommandProtocol.HistoryApp,
        var name => e.Kind == HistoryKind.Device && e.Source == name,
    };

    private FrameworkElement Row(HistoryEntry e)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (e.At > _seenAtOpen)
        {
            grid.Children.Add(new Border
            {
                Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                Background = Res<Brush>("Brush.Signal"),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 5, 0, 0),
                ToolTip = L.CenterUnreadTip,
            });
        }

        var time = new TextBlock
        {
            Text = e.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            Style = Res<Style>("Type.Label"),
            ToolTip = e.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);

        var content = new StackPanel();
        Grid.SetColumn(content, 2);
        grid.Children.Add(content);

        var meta = new WrapPanel();
        meta.Children.Add(KindChip(e));
        meta.Children.Add(new TextBlock
        {
            Text = e.Kind == HistoryKind.Alert ? $"{L.CenterThisPc} · {L.CenterAlertKind(e.App)}"
                : $"{(e.Source is { } source ? DeviceNames.Show(e.SourceKind, source) : "")} · {e.App}",
            Style = Res<Style>("Type.Micro"),
            Margin = new Thickness(6, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (e.Kind == HistoryKind.Alert && string.Equals(e.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            meta.Children.Add(new TextBlock
            {
                Text = " · " + L.CenterSendFailed,
                Style = Res<Style>("Type.Micro"),
                Foreground = Res<Brush>("Brush.StatusBad"),
                Margin = new Thickness(0, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        content.Children.Add(meta);

        content.Children.Add(new TextBlock
        {
            Text = e.Title,
            Style = Res<Style>("Type.BodyStrong"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });

        // A received file's entry opens the folder it was saved to (M-66).
        TextBlock? folderLink = null;
        if (e.Kind == HistoryKind.Device && e.App == TransferHistory.Received)
        {
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(L.CenterOpenFolder));
            link.Click += (_, _) => OpenReceivedFolder();
            folderLink = new TextBlock(link) { Style = Res<Style>("Type.Micro"), Margin = new Thickness(0, 4, 0, 0) };
        }

        if (!string.IsNullOrWhiteSpace(e.Body))
        {
            // One line until clicked; then the whole text, selectable to copy.
            var preview = new TextBlock
            {
                Text = OneLine(e.Body),
                Style = Res<Style>("Type.Body"),
                Foreground = Res<Brush>("Brush.InkSoft"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var full = new TextBox
            {
                Text = e.Body,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Res<Brush>("Brush.Ink"),
                FontSize = 12,
                Padding = new Thickness(0),
                Margin = new Thickness(-2, 2, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            content.Children.Add(preview);
            content.Children.Add(full);
            grid.Cursor = Cursors.Hand;
            grid.ToolTip = L.CenterExpandTip;
            grid.MouseLeftButtonUp += (_, args) =>
            {
                if (args.OriginalSource is DependencyObject d && (IsInside(d, full) || (folderLink is not null && IsInside(d, folderLink))))
                    return; // selecting text, or the folder link
                var open = full.Visibility == Visibility.Visible;
                full.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
                preview.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            };
        }

        if (folderLink is not null) content.Children.Add(folderLink);

        // Transparent background so the whole row, not just its text, takes the click.
        return new Border { Background = Brushes.Transparent, Child = grid };
    }

    private static void OpenReceivedFolder()
    {
        try
        {
            var folder = FileReceiver.Folder();
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TrayLog.Warn("Files", $"Could not open the received files folder: {ex.Message}");
        }
    }

    private Border KindChip(HistoryEntry e)
    {
        string text;
        Brush background;
        Brush foreground = Res<Brush>("Brush.OnDark");
        if (e.Kind == HistoryKind.Alert)
        {
            text = L.CenterKindAlert;
            background = e.Severity switch
            {
                "Critical" => Res<Brush>("Brush.StatusBad"),
                "Warning" => Res<Brush>("Brush.StatusWarn"),
                "Recovery" => Res<Brush>("Brush.StatusOnline"),
                _ => Res<Brush>("Brush.Hairline"),
            };
            if (e.Severity == "Warning") foreground = Res<Brush>("Brush.Carbon");
        }
        else
        {
            var pc = e.SourceKind == DeviceKind.Pc;
            text = pc ? L.DevKindPc : L.DevKindPhone;
            background = Res<Brush>(pc ? "Brush.SystemsTeal" : "Brush.ChromeIndigo");
        }
        return new Border
        {
            Style = Res<Style>("Chip"),
            MinWidth = 48,
            Background = background,
            Child = new TextBlock { Text = text, Style = Res<Style>("ChipText"), Foreground = foreground },
        };
    }

    private void ShowMessage(string text, bool keepFooter = false)
    {
        ListPanel.Children.Clear();
        ListPanel.Children.Add(new Border
        {
            Style = Res<Style>("Card"),
            Child = new TextBlock { Text = text, Style = Res<Style>("Type.Body"), TextWrapping = TextWrapping.Wrap },
        });
        if (keepFooter) return;
        FilterPanel.Children.Clear();
        MoreButton.Visibility = Visibility.Collapsed;
        ClearButton.IsEnabled = false;
        CountText.Text = "";
    }

    private static string DayLabel(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return L.CenterToday;
        if (day == today.AddDays(-1)) return L.CenterYesterday;
        return day.ToString(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "M월 d일 (ddd)" : "ddd, MMM d",
            CultureInfo.CurrentUICulture).ToUpper(CultureInfo.CurrentUICulture);
    }

    private static string OneLine(string s)
    {
        var line = s.ReplaceLineEndings(" ").Trim();
        return line.Length > 300 ? line[..300] : line;
    }

    private static bool IsInside(DependencyObject? d, DependencyObject target)
    {
        while (d is not null)
        {
            if (ReferenceEquals(d, target)) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static bool ForwardingOn()
    {
        try { return TrayConfig.IsNotificationForwardingEnabled(); } catch { return false; }
    }

    private T Res<T>(string key) where T : class => (T)FindResource(key);
}
