namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>What a send started in the Homebase window says about itself (M-80).</summary>
internal enum SendNoticeKind
{
    /// <summary>On its way; stays until the outcome replaces it.</summary>
    Working,
    /// <summary>Delivered (clipboard text).</summary>
    Sent,
    /// <summary>Handed to the Agent (files); how each device fares comes later as a tray balloon.</summary>
    Started,
    /// <summary>Not sent, and why.</summary>
    Failed,
}

/// <summary>
/// Where a send started in the Homebase window (Ctrl+V, a tile's menu, a drop) reports how it goes (M-80): the window's
/// sliding message, in place of the tray balloons a send from the tray menu gets. <see cref="Dropped"/>: the user
/// backed out (closed the picker, said no to a question), so the message goes away without a word.
/// </summary>
internal sealed record SendFeedback(Action<SendNoticeKind, string> Show, Action Dropped);
