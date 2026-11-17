using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// One WPF <see cref="Application"/> for the test run (a process can create only one), on its own STA
/// thread with the Tray's theme loaded, so its windows can be shown as the tray shows them. Tray logs
/// and records go to a temporary folder, never the user's own.
/// </summary>
internal static class Wpf
{
    public static readonly string DataDir = Path.Combine(Path.GetTempPath(), "homebase-tray-tests-" + Environment.ProcessId);

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    private static readonly Lazy<Dispatcher> Ui = new(Start);

    /// <summary>Runs <paramref name="body"/> on the UI thread; fails instead of hanging when it never ends.</summary>
    public static async Task Run(Func<Task> body)
    {
        var work = Ui.Value.InvokeAsync(body).Task.Unwrap();
        if (await Task.WhenAny(work, Task.Delay(Limit)) != work)
            throw new TimeoutException($"The UI work did not finish within {Limit.TotalSeconds} s.");
        await work;
    }

    private static Dispatcher Start()
    {
        Directory.CreateDirectory(DataDir);
        // TrayLog writes to %ProgramData%\Homebase\logs; expanded from this process's environment.
        Environment.SetEnvironmentVariable("ProgramData", DataDir);
        Services.UpdateAutoOutcome.FilePath = Path.Combine(DataDir, "update-auto-outcome.txt");

        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                    new Uri("/Homebase.Tray;component/Themes/HomebaseTheme.xaml", UriKind.Relative)));
                ready.SetResult(app.Dispatcher);
                app.Run();
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        }) { IsBackground = true, Name = "WPF tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
