using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Infrastructure.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogWatcher : IEventLogWatcher
{
    private readonly Dictionary<string, long?> _lastRecordIds = new();

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
            var lastId = _lastRecordIds.GetValueOrDefault(logName);

            using var reader = new EventLogReader(logName, PathType.LogName);
            for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
            {
                if (lastId.HasValue && record.RecordId <= lastId.Value)
                    continue;

                var level = record.Level switch
                {
                    1 => "Critical",
                    2 => "Error",
                    3 => "Warning",
                    4 => "Information",
                    5 => "Verbose",
                    _ => record.LevelDisplayName ?? "Unknown"
                };

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

                if (record.RecordId.HasValue && (!lastId.HasValue || record.RecordId.Value > lastId.Value))
                    lastId = record.RecordId.Value;
            }

            _lastRecordIds[logName] = lastId ?? _lastRecordIds.GetValueOrDefault(logName);
        }
        catch { }
        return items;
    }
}
