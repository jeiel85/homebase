using System.Windows;
using System.Windows.Controls;
using LocalOpsBot.Core.Mobile;

namespace LocalOpsBot.Tray;

/// <summary>
/// Asks for a device's alias for the group (DECISIONS M-49, M-50). <see cref="Ask"/> returns the text to
/// keep — empty for "Original name" — or null when the user cancelled; the Agent's
/// <see cref="DeviceAliasTable.Set"/> then applies the phone app's rules (trim, one space, 40 code points,
/// own name = none).
/// </summary>
public partial class AliasDialog : ThemedWindow
{
    private string? _result;
    private string _last = "";

    private AliasDialog() => InitializeComponent();

    /// <param name="owner">The window to centre on.</param>
    /// <param name="name">The name the device reports.</param>
    /// <param name="alias">Its current alias, or null.</param>
    public static string? Ask(Window? owner, string name, string? alias)
    {
        var dlg = new AliasDialog();
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.Title = L.AliasTitle;
        dlg.HeadingText.Text = L.AliasTitle;
        dlg.ExplainText.Text = L.AliasExplain(name);
        dlg.NoteText.Text = L.AliasGroupNote;
        dlg.AliasBox.Tag = name;
        dlg.SaveButton.Content = L.AliasSave;
        dlg.CancelButton.Content = L.Cancel;
        dlg.ResetButton.Content = L.AliasReset;
        dlg.ResetButton.Visibility = alias is null ? Visibility.Collapsed : Visibility.Visible;
        dlg.AliasBox.Text = alias ?? "";
        dlg.Sync(); // an empty start raises no TextChanged
        dlg.Loaded += (_, _) =>
        {
            dlg.AliasBox.Focus();
            dlg.AliasBox.SelectAll();
        };
        dlg.ShowDialog();
        return dlg._result;
    }

    /// <summary>Keeps the field within 40 code points (an emoji counts once) and updates the counter and hint.</summary>
    private void AliasBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = AliasBox.Text;
        if (DeviceAliasTable.Length(text) > DeviceAliasTable.MaxLength)
        {
            // Like the phone: an edit past the limit is refused. A long paste into an empty field keeps what fits.
            var caret = AliasBox.CaretIndex;
            var keep = _last.Length > 0 ? _last : FirstCodePoints(text, DeviceAliasTable.MaxLength);
            AliasBox.Text = keep;
            AliasBox.CaretIndex = Math.Clamp(caret - (text.Length - keep.Length), 0, keep.Length);
            return; // the assignment raises TextChanged again
        }
        Sync();
    }

    private void Sync()
    {
        _last = AliasBox.Text;
        CounterText.Text = $"{DeviceAliasTable.Length(_last)}/{DeviceAliasTable.MaxLength}";
    }

    private static string FirstCodePoints(string s, int max)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in s.EnumerateRunes().Take(max)) sb.Append(r.ToString());
        return sb.ToString();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _result = AliasBox.Text;
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _result = "";
        Close();
    }
}
