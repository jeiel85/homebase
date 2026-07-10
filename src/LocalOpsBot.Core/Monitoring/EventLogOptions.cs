using System.Linq;
using Microsoft.Extensions.Configuration;

namespace LocalOpsBot.Core.Monitoring;

/// <summary>
/// Event-log monitoring settings. <see cref="Levels"/> controls what is <em>read</em> (and shown by
/// <c>/events</c>); <see cref="AlertLevels"/> controls which of those actually push a Telegram alert.
/// <see cref="RepeatSuppressMinutes"/> throttles repeats so a recurring error doesn't flood.
/// </summary>
public sealed class EventLogOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Which Windows logs to watch (read + alert).</summary>
    public IReadOnlyList<string> Logs { get; set; } = new[] { "Application", "System" };

    /// <summary>Levels collected from the logs — feeds both <c>/events</c> and the alert pipeline.</summary>
    public IReadOnlyList<string> Levels { get; set; } = new[] { "Critical", "Error" };

    /// <summary>
    /// Levels that actually raise a Telegram alert (a subset of <see cref="Levels"/>). Others are
    /// still read and visible in <c>/events</c> but stay silent. Defaults to Critical + Error.
    /// </summary>
    public IReadOnlyList<string> AlertLevels { get; set; } = new[] { "Critical", "Error" };

    public IReadOnlyList<string> ProviderIncludes { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ProviderExcludes { get; set; } = Array.Empty<string>();
    public int MessageMaxChars { get; set; } = 500;

    /// <summary>
    /// A repeating event (same provider + event id) alerts at most once per this many minutes.
    /// Critical events are never suppressed. 0 disables suppression. Defaults to 60.
    /// </summary>
    public int RepeatSuppressMinutes { get; set; } = 60;

    /// <summary>
    /// Binds settings from the <c>eventLog</c> config section. Each list is read explicitly with
    /// REPLACE semantics — the raw configuration binder <em>appends</em> to a non-empty default
    /// collection, which would let a user only extend (never narrow) the lists and could duplicate
    /// entries. Reading each list from its own subsection avoids both.
    /// </summary>
    public static EventLogOptions Bind(IConfiguration section)
    {
        var defaults = new EventLogOptions();
        return new EventLogOptions
        {
            Enabled = section.GetValue("enabled", defaults.Enabled),
            MessageMaxChars = section.GetValue("messageMaxChars", defaults.MessageMaxChars),
            RepeatSuppressMinutes = section.GetValue("repeatSuppressMinutes", defaults.RepeatSuppressMinutes),
            Logs = ReadList(section, "logs", defaults.Logs),
            Levels = ReadList(section, "levels", defaults.Levels),
            AlertLevels = ReadList(section, "alertLevels", defaults.AlertLevels),
            ProviderIncludes = ReadList(section, "providerIncludes", defaults.ProviderIncludes),
            ProviderExcludes = ReadList(section, "providerExcludes", defaults.ProviderExcludes),
        };
    }

    private static IReadOnlyList<string> ReadList(IConfiguration section, string key, IReadOnlyList<string> fallback)
    {
        var sub = section.GetSection(key);
        if (!sub.Exists()) return fallback;                 // not configured — keep the default
        var values = sub.Get<string[]>();
        return values is { Length: > 0 }
            ? values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : fallback;
    }
}
