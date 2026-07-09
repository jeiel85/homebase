using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Monitoring;
using LocalOpsBot.Tests.Fakes;
using Xunit;

namespace LocalOpsBot.Tests.Core.Advisor;

public sealed class PcStateAdvisorTests
{
    private static PcStateAdvisor NewAdvisor(
        LlmAdvisorOptions options, FakeLlmClient llm, ITemperatureCollector? temperature = null) =>
        new(options, llm,
            new FakeSystemMetricsCollector(),
            new FakeDiskCollector(),
            new FakeNetworkStatusChecker(),
            new FakeAlertStore(),
            temperature ?? new FakeTemperatureCollector());

    [Fact]
    public async Task Advise_when_disabled_returns_not_ok()
    {
        var advisor = NewAdvisor(new LlmAdvisorOptions { Enabled = false }, new FakeLlmClient());

        var result = await advisor.AdviseAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("turned off", result.Error!);
    }

    [Fact]
    public async Task Summary_includes_core_metrics()
    {
        var advisor = NewAdvisor(new LlmAdvisorOptions(), new FakeLlmClient());

        var summary = await advisor.BuildStateSummaryAsync(CancellationToken.None);

        Assert.Contains("CPU", summary);
        Assert.Contains("RAM", summary);
        Assert.Contains("Disk", summary);
    }

    [Fact]
    public async Task Summary_includes_temperature_per_kind_max()
    {
        var advisor = NewAdvisor(new LlmAdvisorOptions(), new FakeLlmClient());

        var summary = await advisor.BuildStateSummaryAsync(CancellationToken.None);

        // Hottest sensor per category: CPU max(72,68)=72, GPU 65, Board 41.
        Assert.Contains("CPU temp: 72", summary);
        Assert.Contains("GPU temp: 65", summary);
        Assert.Contains("Board temp: 41", summary);
    }

    [Fact]
    public async Task Summary_omits_temperature_when_no_sensors()
    {
        var noSensors = new FakeTemperatureCollector
        {
            NextResult = CollectorResult<TemperatureSnapshot>.Ok(
                new TemperatureSnapshot(Array.Empty<SensorReading>()), DateTimeOffset.UtcNow)
        };
        var advisor = NewAdvisor(new LlmAdvisorOptions(), new FakeLlmClient(), noSensors);

        var summary = await advisor.BuildStateSummaryAsync(CancellationToken.None);

        Assert.DoesNotContain("temp:", summary);
        Assert.Contains("CPU load", summary); // rest of the summary is unaffected
    }

    [Fact]
    public async Task Advise_feeds_summary_into_prompt_and_returns_llm_text()
    {
        var llm = new FakeLlmClient { NextResult = new(true, "Looks healthy.", null) };
        var advisor = NewAdvisor(new LlmAdvisorOptions(), llm);

        var result = await advisor.AdviseAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Looks healthy.", result.Text);
        Assert.NotNull(llm.LastPrompt);
        Assert.Contains("Current readings", llm.LastPrompt!);
        Assert.Contains("CPU", llm.LastPrompt!);
    }

    [Fact]
    public async Task Prompt_includes_configured_reply_language()
    {
        var llm = new FakeLlmClient();
        var advisor = NewAdvisor(new LlmAdvisorOptions { Language = "Korean" }, llm);

        await advisor.AdviseAsync(CancellationToken.None);

        Assert.Contains("Korean", llm.LastPrompt!);
    }

    [Fact]
    public async Task Advise_propagates_llm_failure()
    {
        var llm = new FakeLlmClient { NextResult = new(false, "", "server down") };
        var advisor = NewAdvisor(new LlmAdvisorOptions(), llm);

        var result = await advisor.AdviseAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("server down", result.Error);
    }
}
