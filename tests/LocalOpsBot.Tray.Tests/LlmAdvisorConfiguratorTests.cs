using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Tray.Services;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// Changing the AI advice model from Settings › AI advice: only <c>llmAdvisor.model</c> changes in the
/// Agent config, and only a name that is safe to hand to <c>ollama pull</c> is accepted.
/// </summary>
public sealed class LlmAdvisorConfiguratorTests
{
    [Theory]
    [InlineData("llama3.2:1b")]
    [InlineData("qwen2.5:3b")]
    [InlineData("library/llama3.2")]
    [InlineData("hf.co/user/model:Q4_K_M")]
    public void Accepts_ollama_model_names(string model) => Assert.True(OllamaSetup.IsValidModelName(model));

    [Theory]
    [InlineData("")]
    [InlineData(" llama3.2")]
    [InlineData("x & calc")]
    [InlineData("llama|calc")]
    [InlineData("\"llama\"")]
    [InlineData("-h")]           // would read as an option of `ollama pull`
    [InlineData(":latest")]
    [InlineData("라마")]          // not ASCII
    public void Rejects_other_names(string model) => Assert.False(OllamaSetup.IsValidModelName(model));

    [Fact]
    public void Rejects_overlong_names() => Assert.False(OllamaSetup.IsValidModelName(new string('a', 201)));

    [Theory]
    [InlineData("qwen2.5:3b", "llama3.2:1b", nameof(ModelChangeCheck.Ok))]
    [InlineData("  qwen2.5:3b  ", "llama3.2:1b", nameof(ModelChangeCheck.Ok))]
    [InlineData("llama3.2:1b", "llama3.2:1b", nameof(ModelChangeCheck.Unchanged))]
    [InlineData(" llama3.2:1b ", "llama3.2:1b", nameof(ModelChangeCheck.Unchanged))]
    [InlineData("", "llama3.2:1b", nameof(ModelChangeCheck.Invalid))]
    [InlineData("x & calc", "llama3.2:1b", nameof(ModelChangeCheck.Invalid))]
    // The enum is internal, so the expected outcome travels by name.
    public void Checks_before_asking_for_approval(string input, string current, string expected) =>
        Assert.Equal(Enum.Parse<ModelChangeCheck>(expected), LlmAdvisorConfigurator.Check(input, current));

    [Fact]
    public void Changes_only_the_model()
    {
        var root = JsonNode.Parse("""
            { "telegram": { "allowedChatIds": [1] },
              "llmAdvisor": { "enabled": true, "endpoint": "http://127.0.0.1:11434", "model": "llama3.2:1b", "maxTokens": 350 } }
            """)!.AsObject();

        LlmAdvisorConfigurator.SetModel(root, "qwen2.5:3b");

        var llm = root["llmAdvisor"]!.AsObject();
        Assert.Equal("qwen2.5:3b", (string?)llm["model"]);
        Assert.Equal("http://127.0.0.1:11434", (string?)llm["endpoint"]);
        Assert.Equal(350, (int?)llm["maxTokens"]);
        Assert.True((bool?)llm["enabled"]);
        Assert.Equal(1, (long?)root["telegram"]!["allowedChatIds"]![0]);
    }

    [Fact]
    public void Adds_the_section_when_the_config_has_none()
    {
        var root = new JsonObject { ["agent"] = new JsonObject { ["language"] = "ko" } };

        LlmAdvisorConfigurator.SetModel(root, "qwen2.5:3b");

        Assert.Equal("qwen2.5:3b", (string?)root["llmAdvisor"]!["model"]);
        Assert.Equal("ko", (string?)root["agent"]!["language"]);
    }

    // The Agent's JSON config reader refuses keys that differ only in case: a second "llmAdvisor" next
    // to "LlmAdvisor" would stop it from starting.
    [Fact]
    public void Keeps_the_spelling_the_file_already_uses()
    {
        var root = JsonNode.Parse("""{ "LlmAdvisor": { "Model": "llama3.2:1b" } }""")!.AsObject();

        LlmAdvisorConfigurator.SetModel(root, "qwen2.5:3b");

        Assert.Single(root);
        var llm = root["LlmAdvisor"]!.AsObject();
        Assert.Single(llm);
        Assert.Equal("qwen2.5:3b", (string?)llm["Model"]);
    }

    [Fact]
    public Task Settings_fills_in_the_saved_model_and_the_pulled_ones() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        Assert.False(settings.SaveModelButton.IsEnabled); // nothing to compare against before a probe

        settings.OllamaPanel.Apply(Probe("llama3.2:1b", "qwen2.5:3b", "llama3.2:1b"));

        Assert.Equal("llama3.2:1b", settings.ModelInput.Text);
        Assert.True(settings.SaveModelButton.IsEnabled);
        Assert.Equal(Visibility.Visible, settings.InstalledModelsRow.Visibility);
        Assert.Equal(new[] { "llama3.2:1b", "qwen2.5:3b" }, ModelLinks(settings).Select(l => ((Run)l.Inlines.FirstInline).Text));

        // A click on a pulled model puts it in the field.
        ModelLinks(settings)[1].DoClick();
        Assert.Equal("qwen2.5:3b", settings.ModelInput.Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_later_probe_keeps_what_was_typed() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        settings.OllamaPanel.Apply(Probe("llama3.2:1b"));
        settings.ModelInput.Text = "qwen2.5:3b";

        settings.OllamaPanel.Apply(Probe("llama3.2:1b"));

        Assert.Equal("qwen2.5:3b", settings.ModelInput.Text);
        Assert.Equal(Visibility.Collapsed, settings.InstalledModelsRow.Visibility);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_later_probe_shows_a_model_changed_elsewhere() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        settings.OllamaPanel.Apply(Probe("llama3.2:1b"));

        settings.OllamaPanel.Apply(Probe("qwen2.5:3b"));

        Assert.Equal("qwen2.5:3b", settings.ModelInput.Text);
        return Task.CompletedTask;
    });

    private static OllamaProbe Probe(string model, params string[] installed) =>
        new(installed.Length > 0 ? OllamaReadiness.Ready : OllamaReadiness.Unreachable,
            model, "http://127.0.0.1:11434", null, installed);

    private static List<Hyperlink> ModelLinks(SettingsPanel settings) =>
        settings.InstalledModelsPanel.Children.OfType<TextBlock>()
            .Select(t => (Hyperlink)t.Inlines.FirstInline).ToList();
}
