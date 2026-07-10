using System.IO;
using System.Text;
using LocalOpsBot.Core.Monitoring;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LocalOpsBot.Tests.Core.Monitoring;

public sealed class EventLogOptionsBindingTests
{
    private static EventLogOptions Bind(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        return EventLogOptions.Bind(config.GetSection("eventLog"));
    }

    [Fact]
    public void Binds_new_alert_levels_and_suppress_window_from_config()
    {
        var opts = Bind("""{ "eventLog": { "alertLevels": ["Critical"], "repeatSuppressMinutes": 30 } }""");

        Assert.Equal(new[] { "Critical" }, opts.AlertLevels);
        Assert.Equal(30, opts.RepeatSuppressMinutes);
        // Untouched fields keep their defaults.
        Assert.Equal(new[] { "Application", "System" }, opts.Logs);
        Assert.Equal(new[] { "Critical", "Error" }, opts.Levels);
    }

    [Fact]
    public void Defaults_apply_when_fields_absent()
    {
        var opts = Bind("""{ "eventLog": { "enabled": true } }""");

        Assert.Equal(new[] { "Critical", "Error" }, opts.AlertLevels);
        Assert.Equal(60, opts.RepeatSuppressMinutes);
        Assert.Equal(new[] { "Application", "System" }, opts.Logs);
    }

    [Fact]
    public void Config_replaces_not_appends_so_lists_can_be_narrowed()
    {
        // The point of the explicit-read fix: setting levels to just Critical yields exactly that,
        // not Critical+Error (the raw binder appends config onto the default collection).
        var opts = Bind("""{ "eventLog": { "levels": ["Critical"], "logs": ["System"] } }""");

        Assert.Equal(new[] { "Critical" }, opts.Levels);
        Assert.Equal(new[] { "System" }, opts.Logs);
    }
}
