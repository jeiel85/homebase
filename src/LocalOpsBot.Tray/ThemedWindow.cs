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
/// Windows drop shadow) on Windows 11. It also keeps windows inside the monitor's work area: a
/// fixed-height window is shrunk to fit a short screen, and a content-sized one grows around its
/// center instead of off the bottom.
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

    // The MaxHeight the window declares; the effective cap is this or the current monitor's
    // work area, whichever is smaller, so a window moved to a taller monitor gets its limit back.
    private double _configuredMaxHeight = double.PositiveInfinity;

    private bool IsContentSized => SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;

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
        _configuredMaxHeight = MaxHeight;
        FitFixedHeight();
    }

    /// <summary>
    /// A fixed-height window (the Homebase window's 680 …) is taller than the work area of a
    /// small screen — a 1366×768 laptop at 125 % has about 580 DIPs — which put its bottom row
    /// (Close / Apply) out of reach (issue #22). Shrink it to the work area; the content between the
    /// header and the buttons already scrolls.
    /// </summary>
    private void FitFixedHeight()
    {
        if (IsContentSized || WindowState != WindowState.Normal || double.IsNaN(Height) || WorkArea() is not { } wa) return;
        if (Height > wa.Height) Height = wa.Height;
        // Once placed (centered at the old height, or near the tray), keep the whole window on screen.
        if (!double.IsNaN(Top) && (Top < wa.Top || Top + Height > wa.Bottom))
            Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
    }

    /// <summary>First paint: the startup position is final now, so make sure it fits (see <see cref="FitFixedHeight"/>).</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        FitFixedHeight();
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
        if (!IsContentSized || WindowState != WindowState.Normal || WorkArea() is not { } wa) return;

        // A changed cap re-measures; the size change that follows places the window.
        if (FitMaxHeight(wa) && sizeInfo.NewSize.Height > MaxHeight) return;

        var top = Top - (sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height) / 2;
        Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - sizeInfo.NewSize.Height));
    }

    /// <summary>
    /// Moving to another monitor changes the work area without a size change (a window already at
    /// its cap does not grow), so recompute the cap here too.
    /// </summary>
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (IsContentSized && WindowState == WindowState.Normal && WorkArea() is { } wa) FitMaxHeight(wa);
    }

    /// <summary>Sets MaxHeight to the configured limit capped at this work area; true if it changed.</summary>
    private bool FitMaxHeight(Rect wa)
    {
        var cap = Math.Min(_configuredMaxHeight, wa.Height);
        if (MaxHeight.Equals(cap)) return false;
        MaxHeight = cap;
        return true;
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
