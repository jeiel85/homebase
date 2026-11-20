using System.Windows;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>Tests that show a window: run alone, as others check that no window of the app is visible.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VisibleWindows
{
    public const string Name = "Visible windows";
}

/// <summary>
/// The Homebase window is prewarmed off screen (PR #114), where the nearest monitor may be a shorter one than the
/// one it will be shown on. A window that is not to fit its work area there keeps its size; the others still shrink
/// to fit (issue #22).
/// </summary>
[Collection(VisibleWindows.Name)]
public sealed class ThemedWindowFitTests
{
    private sealed class OffScreenWindow(bool fits) : ThemedWindow
    {
        protected override bool FitsToWorkArea => fits;
    }

    /// <summary>
    /// As tall as the whole desktop: taller than a work area with a taskbar in it, and still within the size Windows
    /// lets a window have.
    /// </summary>
    private static double Tall => SystemParameters.VirtualScreenHeight;

    private static async Task<double> HeightAfterFirstPaintOffScreen(bool fits)
    {
        var window = new OffScreenWindow(fits)
        {
            Width = 400,
            Height = Tall,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            // A window raises ContentRendered only when it has content.
            Content = new System.Windows.Controls.Border(),
        };
        var rendered = new TaskCompletionSource();
        window.ContentRendered += (_, _) => rendered.TrySetResult();
        try
        {
            window.Show();
            await rendered.Task;
            return window.Height;
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public Task A_window_that_does_not_fit_keeps_its_size_off_screen() => Wpf.Run(async () =>
    {
        Assert.Equal(Tall, await HeightAfterFirstPaintOffScreen(fits: false));
    });

    [Fact]
    public Task Other_windows_still_shrink_to_the_work_area() => Wpf.Run(async () =>
    {
        var height = await HeightAfterFirstPaintOffScreen(fits: true);
        // With one monitor and a taskbar on it, that monitor is the nearest and its work area is shorter.
        var oneMonitor = SystemParameters.VirtualScreenHeight == SystemParameters.PrimaryScreenHeight
            && SystemParameters.VirtualScreenWidth == SystemParameters.PrimaryScreenWidth;
        if (oneMonitor && SystemParameters.WorkArea.Height < SystemParameters.PrimaryScreenHeight)
            Assert.Equal(SystemParameters.WorkArea.Height, height, 0.5);
        else
            Assert.True(height <= Tall);
    });
}
