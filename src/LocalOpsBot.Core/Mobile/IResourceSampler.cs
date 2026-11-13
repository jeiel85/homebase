namespace LocalOpsBot.Core.Mobile;

/// <summary>
/// Per-process resource sampler used only while a phone is watching. <see cref="Start"/> /
/// <see cref="Stop"/> bracket the watching period so nothing runs (no ETW session, no handles)
/// when nobody looks.
/// </summary>
public interface IResourceSampler
{
    /// <summary>True when per-process network bytes are available (ETW session running).</summary>
    bool NetPerProcess { get; }

    void Start();

    void Stop();

    /// <summary>Rates since the previous call. The first call after <see cref="Start"/> returns zero rates.</summary>
    (IReadOnlyList<ProcessSample> Processes, SystemTotals System) Sample();
}
