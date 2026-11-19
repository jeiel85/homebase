using System.Text.Json.Nodes;
using LocalOpsBot.Tray.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// Settings › AI advice › "AI on its own" (DECISIONS M-61): the two switches read from and written to
/// the Agent config, the thresholds shown beside them, and nothing else touched.
/// </summary>
public sealed class AiAutomationConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Both_are_off_and_thresholds_are_the_agents_defaults_when_the_config_is_silent()
    {
        var s = AiAutomationConfig.Read(Config());

        Assert.False(s.AutoAdvice);
        Assert.False(s.InterpretErrors);
        Assert.True(s.EventLogOn);
        Assert.Equal((90d, 90d, 95d, 85d), (s.CpuPercent, s.MemoryPercent, s.DiskPercent, s.TempCelsius));
        Assert.Equal(5, s.SustainMinutes); // 2 breached polls in a row are 300 s apart
        Assert.Equal(60, s.CooldownMinutes);
    }

    [Fact]
    public void Reads_what_the_config_says()
    {
        var s = AiAutomationConfig.Read(Config(
            ("advisorAlerts:enabled", "true"), ("eventLog:llmInterpret", "True"), ("eventLog:enabled", "false"),
            ("advisorAlerts:cpuPercent", "80"), ("advisorAlerts:intervalSeconds", "60"), ("advisorAlerts:consecutiveBreaches", "3")));

        Assert.True(s.AutoAdvice);
        Assert.True(s.InterpretErrors);
        Assert.False(s.EventLogOn);
        Assert.Equal(80, s.CpuPercent);
        Assert.Equal(2, s.SustainMinutes); // 3 polls 60 s apart span 2 minutes
    }

    [Fact]
    public void A_value_that_is_not_a_bool_counts_as_the_default()
    {
        var s = AiAutomationConfig.Read(Config(("advisorAlerts:enabled", "yes"), ("eventLog:enabled", "nope")));
        Assert.False(s.AutoAdvice);
        Assert.True(s.EventLogOn);
    }

    [Fact]
    public void Writes_only_the_two_switches()
    {
        var root = JsonNode.Parse("""
            { "advisorAlerts": { "enabled": false, "cpuPercent": 80 },
              "eventLog": { "enabled": true, "logs": ["System"] },
              "telegram": { "allowedChatIds": [1] } }
            """)!.AsObject();

        AiAutomationConfig.Apply(root, autoAdvice: true, interpretErrors: true);

        Assert.True((bool?)root["advisorAlerts"]!["enabled"]);
        Assert.Equal(80, (int?)root["advisorAlerts"]!["cpuPercent"]);
        Assert.True((bool?)root["eventLog"]!["llmInterpret"]);
        Assert.True((bool?)root["eventLog"]!["enabled"]);
        Assert.Equal("System", (string?)root["eventLog"]!["logs"]![0]);
        Assert.Equal(1, (long?)root["telegram"]!["allowedChatIds"]![0]);
    }

    [Fact]
    public void Adds_the_sections_when_the_config_has_none()
    {
        var root = new JsonObject();
        AiAutomationConfig.Apply(root, autoAdvice: false, interpretErrors: true);

        Assert.False((bool?)root["advisorAlerts"]!["enabled"]);
        Assert.True((bool?)root["eventLog"]!["llmInterpret"]);
    }

    // The Agent's JSON config reader refuses keys that differ only in case (see ElevatedConfig.SetValue).
    [Fact]
    public void Keeps_the_spelling_the_file_already_uses()
    {
        var root = JsonNode.Parse("""{ "AdvisorAlerts": { "Enabled": false }, "EventLog": { "LlmInterpret": false } }""")!.AsObject();

        AiAutomationConfig.Apply(root, autoAdvice: true, interpretErrors: true);

        Assert.Equal(2, root.Count);
        Assert.Single(root["AdvisorAlerts"]!.AsObject());
        Assert.True((bool?)root["AdvisorAlerts"]!["Enabled"]);
        Assert.True((bool?)root["EventLog"]!["LlmInterpret"]);
    }
}
