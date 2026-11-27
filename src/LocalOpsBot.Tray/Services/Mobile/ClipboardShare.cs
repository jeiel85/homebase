using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalOpsBot.Core.Mobile;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDataObject = System.Windows.DataObject;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Clipboard text sent by hand between this PC and the group's devices (DECISIONS M-62). The tray reads the
/// clipboard only when the user picks "Send clipboard", and writes it only when another device sent text —
/// nothing watches the clipboard. The Agent does the network part over the mobile bridge.
/// Call <see cref="ReadAsync"/> and <see cref="PutAsync"/> on the UI (STA) thread.
/// </summary>
internal static class ClipboardShare
{
    // Clipboard formats Windows documents for keeping content private: password managers set them so that
    // clipboard history, cloud sync and other monitors leave the content alone.
    internal const string ExcludeMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string ViewerIgnore = "Clipboard Viewer Ignore";
    internal const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    internal const string CanUploadToCloud = "CanUploadToCloudClipboard";

    private const int BusyRetries = 5;
    private static readonly TimeSpan BusyPause = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// What the clipboard holds: its text (null when none) and whether a password manager marked it; files copied in
    /// Explorer (M-65); a picture as PNG when there is neither (a screenshot, a copied image).
    /// </summary>
    public sealed record Snapshot(string? Text, bool Sensitive, IReadOnlyList<string>? Files = null, byte[]? Png = null);

    /// <summary>A copied picture bigger than this (as PNG) is not sent.</summary>
    private const int MaxPngBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Whether the clipboard's formats mark the content as private (a password manager copied it):
    /// <paramref name="dword"/> reads a format's DWORD value, or null when it has none.
    /// </summary>
    public static bool IsSensitive(IReadOnlyCollection<string> formats, Func<string, int?> dword) =>
        formats.Contains(ExcludeMonitor)
        || formats.Contains(ViewerIgnore)
        || (formats.Contains(CanIncludeInHistory) && dword(CanIncludeInHistory) == 0)
        || (formats.Contains(CanUploadToCloud) && dword(CanUploadToCloud) == 0);

    /// <summary>The clipboard's text, or null when the clipboard stayed busy (another app holds it open).</summary>
    public static async Task<Snapshot?> ReadAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var data = WpfClipboard.GetDataObject();
                if (data is null) return new Snapshot(null, false);
                var formats = data.GetFormats(false);
                var text = data.GetDataPresent(WpfDataFormats.UnicodeText, true) ? data.GetData(WpfDataFormats.UnicodeText, true) as string : null;
                var files = data.GetDataPresent(WpfDataFormats.FileDrop) ? data.GetData(WpfDataFormats.FileDrop) as string[] : null;
                text = string.IsNullOrEmpty(text) ? null : text;
                var png = text is null && files is not { Length: > 0 } ? Png(data) : null;
                return new Snapshot(text, IsSensitive(formats, f => Dword(data, f)), files is { Length: > 0 } ? files : null, png);
            }
            catch (ExternalException) when (attempt < BusyRetries)
            {
                await Task.Delay(BusyPause);
            }
            catch (ExternalException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Puts text another device sent on the clipboard. <paramref name="sensitive"/> keeps it out of clipboard
    /// history (Win+V) and cloud sync, as the sender's password manager asked. False when the clipboard stayed busy.
    /// </summary>
    public static async Task<bool> PutAsync(string text, bool sensitive)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var data = new WpfDataObject();
                data.SetText(text, WpfTextDataFormat.UnicodeText);
                if (sensitive)
                {
                    data.SetData(CanIncludeInHistory, new MemoryStream(BitConverter.GetBytes(0)));
                    data.SetData(CanUploadToCloud, new MemoryStream(BitConverter.GetBytes(0)));
                }
                WpfClipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException) when (attempt < BusyRetries)
            {
                await Task.Delay(BusyPause);
            }
            catch (ExternalException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// <see cref="PutAsync"/> without awaiting, for the short-lived <c>--toast</c> process (held quick text, M-78), which has
    /// no message loop running yet to come back to. The text stays on the clipboard after that process exits.
    /// </summary>
    public static bool PutNow(string text)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var data = new WpfDataObject();
                data.SetText(text, WpfTextDataFormat.UnicodeText);
                WpfClipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException) when (attempt < BusyRetries)
            {
                Thread.Sleep(BusyPause);
            }
            catch (ExternalException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Puts a received picture file on the clipboard (M-64): as PNG (keeps transparency, what browsers and chat apps
    /// paste) and as a bitmap (everything else). False when it can't be decoded or the clipboard stayed busy.
    /// </summary>
    public static async Task<bool> PutImageAsync(string path)
    {
        System.Windows.Media.Imaging.BitmapSource image;
        byte[] png;
        try
        {
            await using var file = File.OpenRead(path);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(file,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            image = decoder.Frames[0];
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            png = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.IO.FileFormatException or ArgumentException
                                       or InvalidOperationException or UnauthorizedAccessException or ExternalException)
        {
            return false; // a format Windows can't decode (HEIC without its codec, …): the file is saved anyway
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var data = new WpfDataObject();
                data.SetImage(image);
                data.SetData("PNG", new MemoryStream(png));
                WpfClipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException) when (attempt < BusyRetries)
            {
                await Task.Delay(BusyPause);
            }
            catch (ExternalException)
            {
                return false;
            }
        }
    }

    /// <summary>Where this PC can send (phones that take clipboard pushes, linked PCs); null when the Agent can't say.</summary>
    public static async Task<IReadOnlyList<ClipTarget>?> TargetsAsync()
    {
        if (MobileHost.Client is not { IsConnected: true } client) return null;
        try
        {
            var reply = await client.RequestAsync(MobileBridge.OpClipTargets);
            if ((bool?)reply["ok"] != true) return null;
            return reply["payload"]?.Deserialize<ClipTargetList>(MobileProtocol.Json)?.Targets ?? Array.Empty<ClipTarget>();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends text to <paramref name="keys"/>; null when the Agent is not reachable or too old.</summary>
    public static async Task<ClipSendResult?> SendAsync(IReadOnlyList<string> keys, string text, bool sensitive)
    {
        if (MobileHost.Client is not { IsConnected: true } client) return null;
        try
        {
            // The Agent waits up to 45 s for every target; a little longer here so its answer arrives.
            var reply = await client.RequestAsync(MobileBridge.OpClipSend, new ClipSendRequest(keys, text, sensitive),
                timeout: TimeSpan.FromSeconds(50));
            if ((bool?)reply["ok"] != true) return null;
            return reply["payload"]?.Deserialize<ClipSendResult>(MobileProtocol.Json);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A copied picture as PNG: the PNG format when there is one (keeps transparency), else the bitmap encoded.</summary>
    private static byte[]? Png(System.Windows.IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream stream && stream.Length is > 0 and <= MaxPngBytes)
                return stream.ToArray();
            if (!data.GetDataPresent(WpfDataFormats.Bitmap)) return null;
            var image = WpfClipboard.GetImage();
            if (image is null) return null;
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.Length <= MaxPngBytes ? buffer.ToArray() : null;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static int? Dword(System.Windows.IDataObject data, string format)
    {
        try
        {
            if (data.GetData(format) is MemoryStream { Length: >= 4 } stream)
            {
                var bytes = stream.ToArray();
                return BitConverter.ToInt32(bytes, 0);
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException) { }
        return null;
    }
}
