using System.Windows;
using System.Windows.Controls;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Services.Mobile;

namespace LocalOpsBot.Tray;

/// <summary>
/// "Send to": the devices that take files, ticked from the last send, for what came from Explorer's "Send to" menu or was
/// dropped on the Homebase window (DECISIONS M-66). Nothing goes out until the user presses Send.
/// </summary>
public partial class SendPickerWindow : ThemedWindow
{
    private readonly List<(CheckBox Box, ClipTarget Target)> _boxes = new();
    private IReadOnlyList<ClipTarget>? _picked;

    private SendPickerWindow() => InitializeComponent();

    /// <summary>
    /// Asks where <paramref name="summary"/> should go, among <paramref name="targets"/>; null when cancelled. Opens by the
    /// mouse pointer and in front, since it comes from Explorer or a drop rather than from a Homebase window.
    /// </summary>
    internal static IReadOnlyList<ClipTarget>? Pick(string summary, IReadOnlyList<ClipTarget> targets, Func<ClipTarget, string> label,
        string noTargets, bool remember = true)
    {
        var w = new SendPickerWindow();
        w.TitleText.Text = L.SendPickTitle.ToUpperInvariant();
        w.SummaryText.Text = summary;
        w.CancelButton.Content = L.Cancel.ToUpperInvariant();
        w.SendButton.Content = L.ClipSendAnyway.ToUpperInvariant();

        var last = (remember ? SendPrefs.LastTargets : Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var anyLast = targets.Any(t => last.Contains(t.Key));
        // The last send's devices first, then the rest as the Agent lists them (phones, then PCs).
        var ordered = targets.OrderBy(t => last.Contains(t.Key) ? 0 : 1).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var target = ordered[i];
            var box = new CheckBox
            {
                Content = label(target),
                IsChecked = anyLast ? last.Contains(target.Key) : i == 0,
                Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 0),
            };
            box.Checked += (_, _) => w.SyncButton();
            box.Unchecked += (_, _) => w.SyncButton();
            w.TargetsPanel.Children.Add(box);
            w._boxes.Add((box, target));
        }
        if (ordered.Count == 0)
        {
            w.EmptyText.Text = noTargets;
            w.EmptyText.Visibility = Visibility.Visible;
        }
        w.SyncButton();
        w.PlaceByPointer();
        w.Loaded += (_, _) =>
        {
            w.Activate();
            (w._boxes.FirstOrDefault(b => b.Box.IsChecked == true).Box ?? (UIElement)w.SendButton).Focus();
        };
        w.ShowDialog();
        if (remember && w._picked is { Count: > 0 } picked) SendPrefs.LastTargets = picked.Select(t => t.Key).ToList();
        return w._picked;
    }

    private void SyncButton() => SendButton.IsEnabled = _boxes.Any(b => b.Box.IsChecked == true);

    private void PlaceByPointer()
    {
        var pointer = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(pointer).WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        // Device pixels to WPF units; the window's height is not known yet, so leave room for a tall one.
        var x = Math.Clamp(pointer.X - 190 * dpi.DpiScaleX, screen.Left, Math.Max(screen.Left, screen.Right - 380 * dpi.DpiScaleX));
        var y = Math.Clamp(pointer.Y - 40 * dpi.DpiScaleY, screen.Top, Math.Max(screen.Top, screen.Bottom - 420 * dpi.DpiScaleY));
        Left = x / dpi.DpiScaleX;
        Top = y / dpi.DpiScaleY;
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        _picked = _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Target).ToList();
        if (_picked.Count == 0) return;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _picked = null;
        Close();
    }
}
