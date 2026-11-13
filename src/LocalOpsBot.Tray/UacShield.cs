using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LocalOpsBot.Tray;

/// <summary>
/// The Windows UAC shield, the platform's own mark for "this asks for administrator approval".
/// Shown on every button that elevates (and on the note above it) so the approval prompt never
/// comes as a surprise. XAML: <c>{x:Static local:UacShield.Source}</c>.
/// </summary>
public static class UacShield
{
    public static ImageSource? Source { get; } = Create();

    private static ImageSource? Create()
    {
        try
        {
            using var icon = System.Drawing.SystemIcons.GetStockIcon(
                System.Drawing.StockIconId.Shield, System.Drawing.StockIconOptions.SmallIcon);
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null; // no shield image; the admin note text still says it
        }
    }
}
