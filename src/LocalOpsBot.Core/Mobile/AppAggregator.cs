namespace LocalOpsBot.Core.Mobile;

/// <summary>One process in a sampling tick. <see cref="CreateTime"/> guards against PID reuse in the parent tree.</summary>
public sealed record ProcessSample(int Pid, int ParentPid, string Name, long CreateTime, ResourceUsage Usage);

/// <summary>
/// Turns a flat process sample + the user's window list into what the phone shows: one entry per
/// window-owning process with usage summed over its window-less descendants, plus a top-N list.
/// Pure so the grouping rules are pinned by tests.
/// </summary>
public static class AppAggregator
{
    public const int MaxApps = 40;
    public const int MaxChildrenPerApp = 20;
    public const int MaxTopProcesses = 30;

    // The shell parents almost everything the user launches; summing its subtree would attribute
    // unrelated background apps to the "File Explorer" window.
    private static readonly HashSet<string> NoSubtree = new(StringComparer.OrdinalIgnoreCase) { "explorer" };

    public static IReadOnlyList<AppEntry> BuildApps(
        IReadOnlyCollection<ProcessSample> processes,
        IReadOnlyCollection<SessionWindow> windows)
    {
        var byPid = new Dictionary<int, ProcessSample>(processes.Count);
        foreach (var p in processes) byPid[p.Pid] = p;

        var children = new Dictionary<int, List<ProcessSample>>();
        foreach (var p in processes)
        {
            if (p.ParentPid == p.Pid || !byPid.TryGetValue(p.ParentPid, out var parent)) continue;
            // A child can't predate its parent; if it does, the parent PID was reused.
            if (parent.CreateTime > p.CreateTime) continue;
            if (!children.TryGetValue(parent.Pid, out var list)) children[parent.Pid] = list = new List<ProcessSample>();
            list.Add(p);
        }

        var windowsByPid = windows
            .Where(w => byPid.ContainsKey(w.Pid))
            .GroupBy(w => w.Pid)
            .ToDictionary(g => g.Key, g => g.ToList());

        var apps = new List<AppEntry>(windowsByPid.Count);
        foreach (var (pid, owned) in windowsByPid)
        {
            var owner = byPid[pid];
            var descendants = new List<ProcessSample>();
            if (!NoSubtree.Contains(owner.Name))
                CollectDescendants(pid, children, windowsByPid, descendants);

            var usage = Sum(descendants.Prepend(owner));
            apps.Add(new AppEntry(
                pid,
                owner.Name,
                owned.Any(w => w.Foreground),
                owned.Select(w => new WindowTitle(w.Title, w.Foreground)).ToList(),
                usage,
                descendants
                    .OrderByDescending(d => d.Usage.CpuPct).ThenByDescending(d => d.Usage.MemBytes)
                    .Take(MaxChildrenPerApp)
                    .Select(ToEntry)
                    .ToList()));
        }

        return apps
            .OrderByDescending(a => a.Foreground)
            .ThenByDescending(a => a.Usage.CpuPct)
            .ThenByDescending(a => a.Usage.MemBytes)
            .Take(MaxApps)
            .ToList();
    }

    public static IReadOnlyList<ProcessEntry> TopProcesses(IReadOnlyCollection<ProcessSample> processes) =>
        processes
            .Where(p => p.Pid != 0) // System Idle Process
            .OrderByDescending(p => p.Usage.CpuPct)
            .ThenByDescending(p => p.Usage.MemBytes)
            .Take(MaxTopProcesses)
            .Select(ToEntry)
            .ToList();

    private static void CollectDescendants(
        int pid,
        Dictionary<int, List<ProcessSample>> children,
        Dictionary<int, List<SessionWindow>> windowOwners,
        List<ProcessSample> into)
    {
        var stack = new Stack<int>();
        stack.Push(pid);
        var seen = new HashSet<int> { pid };
        while (stack.Count > 0)
        {
            if (!children.TryGetValue(stack.Pop(), out var kids)) continue;
            foreach (var kid in kids)
            {
                // A descendant with its own window is listed as its own app, subtree included.
                if (windowOwners.ContainsKey(kid.Pid) || !seen.Add(kid.Pid)) continue;
                into.Add(kid);
                stack.Push(kid.Pid);
            }
        }
    }

    private static ResourceUsage Sum(IEnumerable<ProcessSample> samples)
    {
        double cpu = 0;
        long mem = 0, io = 0, rx = 0, tx = 0;
        bool hasNet = false;
        foreach (var s in samples)
        {
            cpu += s.Usage.CpuPct;
            mem += s.Usage.MemBytes;
            io += s.Usage.IoBps;
            if (s.Usage.NetRxBps is { } r) { rx += r; hasNet = true; }
            if (s.Usage.NetTxBps is { } t) { tx += t; hasNet = true; }
        }
        return new ResourceUsage(Math.Round(cpu, 1), mem, io, hasNet ? rx : null, hasNet ? tx : null);
    }

    private static ProcessEntry ToEntry(ProcessSample p) => new(p.Pid, p.ParentPid, p.Name, p.Usage);
}
