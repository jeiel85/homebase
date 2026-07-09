using System.Windows;

namespace LocalOpsBot.Tray;

/// <summary>
/// A small modal dialog drawn with the Homebase theme, used in place of the native Windows
/// <c>MessageBox</c> so update prompts and notices match the rest of the app.
/// </summary>
public partial class MessageDialog : Window
{
    private MessageDialog() => InitializeComponent();

    /// <summary>True when the user clicked the primary button (e.g. confirmed the action).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>
    /// Shows a themed modal dialog. With only <paramref name="primary"/> it is an OK-style notice;
    /// pass <paramref name="secondary"/> to make it a confirm — the return value is <c>true</c> when
    /// the user clicks the primary button.
    /// </summary>
    public static bool Show(string title, string message, string primary = "OK", string? secondary = null)
    {
        var dlg = new MessageDialog();
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

    private void Primary_Click(object sender, RoutedEventArgs e) { Confirmed = true; Close(); }

    private void Secondary_Click(object sender, RoutedEventArgs e) { Confirmed = false; Close(); }
}
