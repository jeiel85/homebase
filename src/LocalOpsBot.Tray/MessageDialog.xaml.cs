using System.Windows;

namespace LocalOpsBot.Tray;

/// <summary>
/// A small modal dialog drawn with the Homebase theme, used in place of the native Windows
/// <c>MessageBox</c> so update prompts and notices match the rest of the app.
/// </summary>
public partial class MessageDialog : ThemedWindow
{
    private MessageDialog() => InitializeComponent();

    /// <summary>True when the user clicked the primary button (e.g. confirmed the action).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>
    /// Shows a themed modal dialog. With only <paramref name="primary"/> it is an OK-style notice;
    /// pass <paramref name="secondary"/> to make it a confirm — the return value is <c>true</c> when
    /// the user clicks the primary button.
    /// <para>
    /// <paramref name="owner"/> is the window it belongs to (the Homebase window for its tabs); without
    /// one, the app's active window is used. An owned dialog stays above its owner and centres on it,
    /// so it can't open behind it when the tray has lost the foreground (#71).
    /// </para>
    /// </summary>
    public static bool Show(string title, string message, string? primary = null, string? secondary = null, Window? owner = null)
    {
        primary ??= L.Ok;
        var dlg = new MessageDialog();
        if (OwnerFor(owner) is { } o)
        {
            dlg.Owner = o;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        dlg.TitleText.Text = title.ToUpperInvariant();
        dlg.MessageText.Text = message;
        dlg.PrimaryButton.Content = primary.ToUpperInvariant();
        if (secondary is not null)
        {
            dlg.SecondaryButton.Content = secondary.ToUpperInvariant();
            dlg.SecondaryButton.Visibility = Visibility.Visible;
        }
        dlg.ShowDialog();
        return dlg.Confirmed;
    }

    /// <summary>The given window, else the app's active one; only a window that is shown can own a dialog.</summary>
    private static Window? OwnerFor(Window? requested)
    {
        static bool Usable(Window w) => w.IsLoaded && w.IsVisible;
        if (requested is not null) return Usable(requested) ? requested : null;
        return System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && Usable(w));
    }

    private void Primary_Click(object sender, RoutedEventArgs e) { Confirmed = true; Close(); }

    private void Secondary_Click(object sender, RoutedEventArgs e) { Confirmed = false; Close(); }
}
