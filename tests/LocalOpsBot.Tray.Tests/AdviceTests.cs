using System.Windows;
using LocalOpsBot.Core.Advisor;
using LocalOpsBot.Core.Mobile;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// Settings › AI advice › "Get advice now" (DECISIONS M-59): the Agent's reply shown as plain text with
/// what the model saw, or a failure in the tray's own words that points at the step above.
/// </summary>
public sealed class AdviceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 19, 1, 30, 0, TimeSpan.Zero);

    [Fact]
    public Task Advice_shows_as_plain_text_with_its_basis() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        settings.ShowAdvice(new AdviceResponse(1, true, "- <b>Close</b> Chrome.", "CPU load: 92%", "llama3.2:1b", At, Cached: true));

        Assert.Equal(Visibility.Visible, settings.AdviceBox.Visibility);
        Assert.Equal("- <b>Close</b> Chrome.", settings.AdviceText.Text); // no markup is interpreted
        Assert.Equal("CPU load: 92%", settings.AdviceBasis.Text);
        Assert.Equal(L.AiAskMeta("llama3.2:1b", At.ToLocalTime(), true), settings.AdviceMeta.Text);
        Assert.Equal("", settings.AdviceStatusText.Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_failure_hides_the_old_advice_and_says_what_to_do() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        settings.ShowAdvice(new AdviceResponse(1, true, "Fine.", "s", "m", At, false));

        // (Timeout: unreachable / model_missing would also re-probe Ollama on this machine.)
        settings.ShowAdvice(new AdviceResponse(1, false, null, null, "llama3.2:1b", At, false,
            AdviceErrors.Timeout, "The LLM did not respond within 60s."));

        Assert.Equal(Visibility.Collapsed, settings.AdviceBox.Visibility);
        Assert.Equal(L.AiAskError(AdviceErrors.Timeout, null), settings.AdviceStatusText.Text);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(AdviceErrors.Disabled)]
    [InlineData(AdviceErrors.Unreachable)]
    [InlineData(AdviceErrors.ModelMissing)]
    [InlineData(AdviceErrors.Timeout)]
    public void Each_known_failure_has_its_own_wording(string code) =>
        Assert.DoesNotContain("detail", L.AiAskError(code, "detail"));

    [Fact]
    public void An_unknown_failure_shows_the_detail() =>
        Assert.Contains("out of memory", L.AiAskError(AdviceErrors.ServerError, "out of memory"));
}
