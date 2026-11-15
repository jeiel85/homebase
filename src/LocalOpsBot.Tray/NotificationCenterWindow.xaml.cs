using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Tray.Services;
using LocalOpsBot.Tray.Services.Mobile;

namespace LocalOpsBot.Tray;

/// <summary>
/// The PC's notification center (DECISIONS M-32): notifications other devices relayed here and this
/// PC's alerts, newest first, grouped by day. Filters are by source; a row expands to its full text.
/// Opening it marks everything shown as read; new arrivals while it is open are added at the top.
/// </summary>
public partial class NotificationCenterWindow : ThemedWindow
{
    private const int PageSize = 50;
    private const string AlertFilter = "\u0001alert";

    private readonly List<HistoryEntry> _entries = new();
    private readonly DateTimeOffset _seenAtOpen;
    private bool _more;
    private bool _loading;
    private string? _filter; // null = all, AlertFilter, or a device name

    public NotificationCenterWindow()
    {
        InitializeComponent();
        _seenAtOpen = NotificationCenter.LastSeen();
        Loaded += async (_, _) => await ReloadAsync();
        MobileHost.ToastRequested += OnToastRequested;
        Closed += (_, _) => MobileHost.ToastRequested -= OnToastRequested;
    }

    private void OnToastRequested(TrayToast _) =>
        // The Agent writes the history before it sends the balloon, so the new entry is already there.
        Dispatcher.BeginInvoke(new Action(async () => await AddNewestAsync()));

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
            MarkShownAsSeen();
            Render();
        }
        finally { _loading = false; }
    }

    private async Task AddNewestAsync()
    {
        if (_loading || !IsLoaded) return;
        var (page, _) = await NotificationCenter.ListAsync(null, PageSize);
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
            var (page, _) = await NotificationCenter.ListAsync(_entries[^1].At, PageSize);
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
        if (!MessageDialog.Show(L.CenterClearTitle, L.CenterClearMessage, L.CenterClearConfirm, L.Cancel)) return;
        if (!await NotificationCenter.ClearAsync())
        {
            MessageDialog.Show(L.CenterClearTitle, L.CenterFailed);
            return;
        }
        if (_filter is not null && _filter != AlertFilter) _filter = null;
        await ReloadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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

    private void RenderFilters()
    {
        FilterPanel.Children.Clear();
        FilterPanel.Children.Add(FilterChip(L.CenterAll, null));
        if (_entries.Any(e => e.Kind == HistoryKind.Alert)) FilterPanel.Children.Add(FilterChip(L.CenterAlerts, AlertFilter));
        foreach (var source in _entries.Where(e => e.Kind == HistoryKind.Device && e.Source is not null)
                     .Select(e => e.Source!).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCulture))
            FilterPanel.Children.Add(FilterChip(source, source));
    }

    private Button FilterChip(string label, string? key)
    {
        var selected = _filter == key;
        var chip = new Button
        {
            Content = label,
            Style = Res<Style>(selected ? "Button.Amber" : "Button.Carbon"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
        };
        chip.Click += (_, _) => { _filter = key; Render(); };
        return chip;
    }

    private bool Matches(HistoryEntry e) => _filter switch
    {
        null => true,
        AlertFilter => e.Kind == HistoryKind.Alert,
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
            Text = e.Kind == HistoryKind.Alert ? $"{L.CenterThisPc} · {L.CenterAlertKind(e.App)}" : $"{e.Source} · {e.App}",
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
                if (args.OriginalSource is DependencyObject d && IsInside(d, full)) return; // selecting text
                var open = full.Visibility == Visibility.Visible;
                full.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
                preview.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            };
        }

        // Transparent background so the whole row, not just its text, takes the click.
        return new Border { Background = Brushes.Transparent, Child = grid };
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
