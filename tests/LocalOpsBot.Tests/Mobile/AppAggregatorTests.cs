using LocalOpsBot.Core.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

public class AppAggregatorTests
{
    private static ProcessSample P(int pid, int ppid, string name, double cpu = 0, long mem = 0, long create = 100) =>
        new(pid, ppid, name, create, new ResourceUsage(cpu, mem, 0, null, null));

    [Fact]
    public void Terminal_window_aggregates_windowless_descendants()
    {
        var procs = new[]
        {
            P(10, 1, "WindowsTerminal", cpu: 1, mem: 100),
            P(11, 10, "pwsh", cpu: 2, mem: 50, create: 110),
            P(12, 11, "node", cpu: 30, mem: 500, create: 120),
        };
        var apps = AppAggregator.BuildApps(procs, new[] { new SessionWindow(1, 10, "Build app", true) });

        var app = Assert.Single(apps);
        Assert.Equal("WindowsTerminal", app.Process);
        Assert.Equal(33, app.Usage.CpuPct);
        Assert.Equal(650, app.Usage.MemBytes);
        Assert.Equal(new[] { "node", "pwsh" }, app.Children.Select(c => c.Name));
        Assert.True(app.Foreground);
    }

    [Fact]
    public void Descendant_with_its_own_window_is_listed_separately_and_not_double_counted()
    {
        var procs = new[]
        {
            P(10, 1, "Code", cpu: 5),
            P(20, 10, "chrome", cpu: 40, create: 150),
            P(21, 20, "chrome", cpu: 10, create: 160),
        };
        var windows = new[] { new SessionWindow(1, 10, "main.cs - VS Code", false), new SessionWindow(2, 20, "Docs - Chrome", true) };
        var apps = AppAggregator.BuildApps(procs, windows);

        Assert.Equal(2, apps.Count);
        Assert.Equal("chrome", apps[0].Process); // foreground first
        Assert.Equal(50, apps[0].Usage.CpuPct);
        Assert.Equal(5, apps[1].Usage.CpuPct);   // Code does not absorb chrome's subtree
    }

    [Fact]
    public void Reused_parent_pid_is_not_treated_as_parent()
    {
        // pid 10 was created after its "child" 11 → 11's real parent died and the pid was reused.
        var procs = new[] { P(10, 1, "app", cpu: 1, create: 500), P(11, 10, "orphan", cpu: 50, create: 200) };
        var app = Assert.Single(AppAggregator.BuildApps(procs, new[] { new SessionWindow(1, 10, "App", false) }));
        Assert.Equal(1, app.Usage.CpuPct);
        Assert.Empty(app.Children);
    }

    [Fact]
    public void Explorer_does_not_absorb_everything_it_launched()
    {
        var procs = new[] { P(5, 1, "explorer", cpu: 1), P(6, 5, "OneDrive", cpu: 9, create: 150) };
        var app = Assert.Single(AppAggregator.BuildApps(procs, new[] { new SessionWindow(1, 5, "Downloads", false) }));
        Assert.Equal(1, app.Usage.CpuPct);
    }

    [Fact]
    public void Multiple_windows_of_one_process_form_one_app_and_net_stays_null_without_etw()
    {
        var procs = new[] { P(10, 1, "chrome", cpu: 3) };
        var app = Assert.Single(AppAggregator.BuildApps(procs, new[]
        {
            new SessionWindow(1, 10, "A - Chrome", false), new SessionWindow(2, 10, "B - Chrome", false),
        }));
        Assert.Equal(2, app.Windows.Count);
        Assert.Null(app.Usage.NetRxBps);
    }

    [Fact]
    public void Windows_of_exited_processes_are_dropped_and_top_processes_skip_idle()
    {
        var procs = new[] { P(0, 0, "[System Process]", cpu: 90), P(10, 1, "a", cpu: 1) };
        Assert.Empty(AppAggregator.BuildApps(procs, new[] { new SessionWindow(1, 999, "gone", false) }));
        Assert.Equal(new[] { 10 }, AppAggregator.TopProcesses(procs).Select(p => p.Pid));
    }
}
