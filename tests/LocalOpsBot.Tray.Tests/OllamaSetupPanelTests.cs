using System.Windows;
using System.Windows.Controls;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Tray.Services;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// The local AI (Ollama) steps — start it, pull the model — used to live only in the setup checklist,
/// so once it was closed Settings had no way back to them. Settings now has an AI advice area with the
/// same row as the checklist.
/// </summary>
public sealed class OllamaSetupPanelTests
{
    private const string Endpoint = "http://127.0.0.1:11434";

    [Fact]
    public Task Settings_has_an_ai_area_with_the_ollama_row() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        settings.ShowTab(SettingsTab.Ai);

        Assert.Same(settings.AiTab, settings.Tabs.SelectedItem);
        Assert.Equal(L.TabAi, settings.AiTab.Header);
        Assert.True(settings.OllamaPanel.ShowConfig);
        Assert.Equal("Ollama", settings.OllamaPanel.Title);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Setup_checklist_uses_the_same_row() => Wpf.Run(() =>
    {
        // Never shown, and not closed: closing marks onboarding done in the real %LOCALAPPDATA%.
        var onboarding = new OnboardingWindow();
        Assert.IsType<OllamaSetupPanel>(onboarding.OllamaPanel);
        Assert.False(onboarding.OllamaPanel.ShowConfig);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Model_missing_offers_the_pull() => Wpf.Run(() =>
    {
        var panel = new OllamaSetupPanel { ShowConfig = true };
        panel.Apply(new OllamaProbe(OllamaReadiness.ModelMissing, "llama3.2:1b", Endpoint, null));

        Assert.Equal(Visibility.Visible, panel.PullHint.Visibility);
        Assert.Equal("llama3.2:1b", panel.PullModelText.Text);
        Assert.False(panel.PullModelText.IsReadOnly);
        Assert.True(panel.PullButton.IsEnabled);
        Assert.Equal(Visibility.Collapsed, panel.StartButton.Visibility);
        Assert.Equal(Visibility.Collapsed, panel.InstallHint.Visibility);
        Assert.Equal(Visibility.Visible, panel.ConfigText.Visibility);
        Assert.Equal(L.OllamaConfig("llama3.2:1b", Endpoint), panel.ConfigText.Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Installed_models_offer_selection_instead_of_an_unneeded_download() => Wpf.Run(() =>
    {
        var panel = new OllamaSetupPanel { ShowConfig = true };
        panel.Apply(new OllamaProbe(OllamaReadiness.ModelMissing, "llama3.2:1b", Endpoint, null, ["qwen3.5:4b"]));

        Assert.Equal(L.OllamaChooseInstalled, panel.StatusText.Text);
        Assert.Equal(Visibility.Collapsed, panel.PullHint.Visibility);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Download_accepts_a_valid_model_name_and_rejects_an_invalid_one() => Wpf.Run(() =>
    {
        var panel = new OllamaSetupPanel { ShowConfig = true };
        panel.Apply(new OllamaProbe(OllamaReadiness.ModelMissing, "llama3.2:1b", Endpoint, null, []));
        panel.PullModelText.Text = "qwen3.5:4b";
        Assert.True(panel.PullButton.IsEnabled);
        panel.PullModelText.Text = "x & calc";
        Assert.False(panel.PullButton.IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_model_name_that_could_reach_the_shell_is_not_pulled() => Wpf.Run(() =>
    {
        var panel = new OllamaSetupPanel();
        panel.Apply(new OllamaProbe(OllamaReadiness.ModelMissing, "x & calc", Endpoint, null));

        Assert.False(panel.PullButton.IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Ready_needs_no_step() => Wpf.Run(() =>
    {
        var panel = new OllamaSetupPanel();
        panel.Apply(new OllamaProbe(OllamaReadiness.Ready, "llama3.2:1b", Endpoint, null));

        Assert.Equal(L.OllamaReady("llama3.2:1b"), panel.StatusText.Text);
        Assert.Equal(Visibility.Collapsed, panel.PullHint.Visibility);
        Assert.Equal(Visibility.Collapsed, panel.StartButton.Visibility);
        Assert.Equal(Visibility.Collapsed, panel.InstallHint.Visibility);
        // The checklist leaves the configured model/server out; only Settings shows it.
        Assert.Equal(Visibility.Collapsed, panel.ConfigText.Visibility);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Unreachable_offers_start_or_install() => Wpf.Run(() =>
    {
        var panel = new OllamaSetupPanel();
        panel.Apply(new OllamaProbe(OllamaReadiness.Unreachable, "llama3.2:1b", Endpoint, null));

        // Which one depends on whether Ollama is installed on the machine running the tests.
        var installed = OllamaSetup.IsInstalled();
        Assert.Equal(installed ? Visibility.Visible : Visibility.Collapsed, panel.StartButton.Visibility);
        Assert.Equal(installed ? Visibility.Collapsed : Visibility.Visible, panel.InstallHint.Visibility);
        Assert.Equal(Visibility.Collapsed, panel.PullHint.Visibility);
        return Task.CompletedTask;
    });
}
