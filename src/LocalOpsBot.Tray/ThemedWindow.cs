using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace LocalOpsBot.Tray;

/// <summary>
/// Base window that wears the Homebase custom title-bar chrome (see the <c>Window.Chrome</c> style
/// in HomebaseTheme.xaml). Title-bar dragging and Aero-Snap stay native via
/// <see cref="System.Windows.Shell.WindowChrome"/>; this wires the themed minimize/close caption
/// buttons drawn in the template, and asks DWM to round the corners (which also draws the standard
/// Windows drop shadow) on Windows 11.
/// </summary>
public class ThemedWindow : Window
{
    // DWM window attributes (Windows 11 build 22000+). Rounding a borderless WindowChrome window
    // also gives it the standard drop shadow; on Windows 10 the attribute is a harmless no-op.
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { /* pre-Win11 or unsupported: corners stay square, no harm */ }
    }

    /// <summary>
    /// A content-sized window (<see cref="SizeToContent"/> Height) is placed once, at its first
    /// size; content that fills in afterwards (async status, a QR code, lists) would grow it
    /// downward only, past the bottom of the screen. Grow it around its vertical center instead,
    /// cap it at the work area's height (the inner ScrollViewer takes the rest) and keep it on screen.
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!sizeInfo.HeightChanged || sizeInfo.PreviousSize.Height <= 0) return;
        if (SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight)) return;
        if (WindowState != WindowState.Normal || WorkArea() is not { } wa) return;

        if (MaxHeight > wa.Height)
        {
            MaxHeight = wa.Height; // re-measures; the next size change places the capped window
            if (sizeInfo.NewSize.Height > wa.Height) return;
        }

        var top = Top - (sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height) / 2;
        Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - sizeInfo.NewSize.Height));
    }

    /// <summary>Work area (DIPs) of the monitor this window is on — not just the primary one.</summary>
    private Rect? WorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return null;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return null;
        var toDip = target.TransformFromDevice;
        return new Rect(
            toDip.Transform(new Point(info.rcWork.Left, info.rcWork.Top)),
            toDip.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Wire("PART_Minimize", () => WindowState = WindowState.Minimized);
        Wire("PART_Close", Close);

        // Dialogs (NoResize) get a close button only. Done here rather than with a template
        // trigger — a TargetName trigger inside this Window ControlTemplate trips a WPF
        // template-parser NullReferenceException.
        if (ResizeMode == ResizeMode.NoResize && GetTemplateChild("PART_Minimize") is UIElement min)
            min.Visibility = Visibility.Collapsed;
    }

    private void Wire(string partName, Action action)
    {
        if (GetTemplateChild(partName) is Button button)
            button.Click += (_, _) => action();
    }
}
