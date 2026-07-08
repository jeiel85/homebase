using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Infrastructure.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogWatcher : IEventLogWatcher
{
    // Per-log resume position. Reading resumes strictly AFTER this bookmark, so each poll
    // only scans events written since the last one — instead of walking the whole log and
    // skipping by RecordId every time (which is O(log size) on every poll for large logs).
    private readonly Dictionary<string, EventBookmark?> _bookmarks = new();

    public Task<IReadOnlyList<WindowsEventLogItem>> PollAsync(EventLogOptions options, CancellationToken ct)
    {
        var results = new List<WindowsEventLogItem>();
        foreach (var logName in options.Logs)
            results.AddRange(ReadLog(logName, options));
        return Task.FromResult<IReadOnlyList<WindowsEventLogItem>>(results);
    }

    private List<WindowsEventLogItem> ReadLog(string logName, EventLogOptions options)
    {
        var items = new List<WindowsEventLogItem>();
        try
        {
            // First poll for this log: record the newest event as a baseline and emit
            // nothing, so a restart doesn't replay historical errors as an alert storm.
            if (!_bookmarks.ContainsKey(logName))
            {
                _bookmarks[logName] = ReadBaselineBookmark(logName);
                return items;
            }

            var query = new EventLogQuery(logName, PathType.LogName);
            var bookmark = _bookmarks[logName];

            EventLogReader reader;
            try
            {
                // A bookmark resumes after the last-seen event; a null bookmark means the log
                // was empty at baseline, so reading from the start yields only genuinely-new events.
                reader = bookmark is null
                    ? new EventLogReader(query)
                    : new EventLogReader(query, bookmark);
            }
            catch (EventLogException)
            {
                // The bookmark is no longer valid (log cleared or overwritten). Re-baseline
                // to the current newest event and skip this cycle rather than replay history.
                _bookmarks[logName] = ReadBaselineBookmark(logName);
                return items;
            }

            using (reader)
            {
                for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                {
                    using (record)
                    {
                        // Advance the resume point for every event read, even ones filtered out
                        // below, so the next poll never re-scans them.
                        _bookmarks[logName] = record.Bookmark;

                        var level = MapLevel(record);
                        if (!options.Levels.Contains(level, StringComparer.OrdinalIgnoreCase))
                            continue;

                        var msg = record.FormatDescription() ?? string.Empty;
                        if (options.MessageMaxChars > 0 && msg.Length > options.MessageMaxChars)
                            msg = msg[..options.MessageMaxChars] + "...";

                        items.Add(new WindowsEventLogItem(
                            logName, record.RecordId.GetValueOrDefault(), record.Id,
                            record.ProviderName, level,
                            record.TimeCreated?.ToUniversalTime() ?? DateTimeOffset.UtcNow,
                            record.MachineName, string.IsNullOrEmpty(msg) ? null : msg));
                    }
                }
            }
        }
        catch { }
        return items;
    }

    // The bookmark of the current newest event in the log (null if empty or unreadable),
    // used as the "watch from here" baseline so history is never replayed.
    private static EventBookmark? ReadBaselineBookmark(string logName)
    {
        try
        {
            var newestQuery = new EventLogQuery(logName, PathType.LogName) { ReverseDirection = true };
            using var newestReader = new EventLogReader(newestQuery);
            using var newest = newestReader.ReadEvent();
            return newest?.Bookmark;
        }
        catch
        {
            return null;
        }
    }

    private static string MapLevel(EventRecord record) => record.Level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        4 => "Information",
        5 => "Verbose",
        _ => record.LevelDisplayName ?? "Unknown"
    };
}
