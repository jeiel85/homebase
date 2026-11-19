using System.Text.Json.Nodes;
using System.Windows;
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
    [InlineData("Llama3.2:1B", "llama3.2:1b", nameof(ModelChangeCheck.Unchanged))] // Ollama ignores case
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
    public void Offers_the_pulled_models_by_name_with_the_configured_one_as_current()
    {
        var c = LlmAdvisorConfigurator.ChoicesFor("llama3.2:1b", ["qwen2.5:3b", "llama3.2:1b", "Gemma3:1b"]);

        Assert.Equal(new[] { "Gemma3:1b", "llama3.2:1b", "qwen2.5:3b" }, c.Models);
        Assert.Equal("llama3.2:1b", c.Current);
        Assert.True(c.CurrentPulled);
    }

    // The readiness probe already treats a bare configured name as its ":latest" tag; picking that
    // entry must not count as a change worth an approval prompt.
    [Theory]
    [InlineData("llama3.2", "llama3.2:latest")]
    [InlineData("LLAMA3.2:1B", "llama3.2:1b")]
    public void The_current_entry_is_the_pulled_model_the_config_names(string configured, string entry)
    {
        var c = LlmAdvisorConfigurator.ChoicesFor(configured, [entry, "qwen2.5:3b"]);

        Assert.Equal(entry, c.Current);
        Assert.Equal(2, c.Models.Count);
        Assert.True(c.CurrentPulled);
    }

    [Fact]
    public void Lists_a_configured_model_that_is_not_pulled_first()
    {
        var c = LlmAdvisorConfigurator.ChoicesFor("llama3.2:1b", ["qwen2.5:3b"]);

        Assert.Equal(new[] { "llama3.2:1b", "qwen2.5:3b" }, c.Models);
        Assert.Equal("llama3.2:1b", c.Current);
        Assert.False(c.CurrentPulled);
    }

    [Fact]
    public void Offers_only_the_configured_model_when_nothing_is_reported()
    {
        var c = LlmAdvisorConfigurator.ChoicesFor("llama3.2:1b", []);

        Assert.Equal(new[] { "llama3.2:1b" }, c.Models);
        Assert.False(c.CurrentPulled);
    }

    [Fact]
    public void Drops_duplicate_and_blank_names()
    {
        var c = LlmAdvisorConfigurator.ChoicesFor("qwen2.5:3b", ["qwen2.5:3b", "QWEN2.5:3B", " ", ""]);

        Assert.Equal(new[] { "qwen2.5:3b" }, c.Models);
    }

    [Fact]
    public Task Settings_picks_the_model_in_use_from_the_pulled_ones() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        Assert.False(settings.ModelPicker.IsEnabled); // nothing to pick before a probe
        Assert.False(settings.SaveModelButton.IsEnabled);

        settings.OllamaPanel.Apply(Probe("llama3.2:1b", "qwen2.5:3b", "llama3.2:1b"));

        Assert.True(settings.ModelPicker.IsEnabled);
        Assert.Equal(new[] { "llama3.2:1b", "qwen2.5:3b" }, Picks(settings).Select(p => p.Name));
        Assert.Equal("llama3.2:1b", Picked(settings));
        Assert.Contains(L.AiModelInUse, Picks(settings)[0].Note);
        Assert.Equal("", Picks(settings)[1].Note);
        Assert.Equal(Visibility.Collapsed, settings.AiModelNoneText.Visibility);
        Assert.False(settings.SaveModelButton.IsEnabled); // the model in use is no change

        // Picking another pulled model offers SAVE; picking the one in use again takes it back.
        settings.ModelPicker.SelectedIndex = 1;
        Assert.True(settings.SaveModelButton.IsEnabled);
        settings.ModelPicker.SelectedIndex = 0;
        Assert.False(settings.SaveModelButton.IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_later_probe_keeps_an_unsaved_pick() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        settings.OllamaPanel.Apply(Probe("llama3.2:1b", "llama3.2:1b", "qwen2.5:3b"));
        settings.ModelPicker.SelectedIndex = 1;

        settings.OllamaPanel.Apply(Probe("llama3.2:1b", "llama3.2:1b", "qwen2.5:3b", "gemma3:1b"));

        Assert.Equal("qwen2.5:3b", Picked(settings));
        Assert.True(settings.SaveModelButton.IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_pick_the_server_no_longer_has_falls_back_to_the_model_in_use() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        settings.OllamaPanel.Apply(Probe("llama3.2:1b", "llama3.2:1b", "qwen2.5:3b"));
        settings.ModelPicker.SelectedIndex = 1;

        settings.OllamaPanel.Apply(Probe("llama3.2:1b", "llama3.2:1b"));

        Assert.Equal("llama3.2:1b", Picked(settings));
        Assert.False(settings.SaveModelButton.IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_later_probe_shows_a_model_changed_elsewhere() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        settings.OllamaPanel.Apply(Probe("llama3.2:1b", "llama3.2:1b", "qwen2.5:3b"));

        settings.OllamaPanel.Apply(Probe("qwen2.5:3b", "llama3.2:1b", "qwen2.5:3b"));

        Assert.Equal("qwen2.5:3b", Picked(settings));
        Assert.Contains(L.AiModelInUse, Picks(settings)[1].Note);
        Assert.False(settings.SaveModelButton.IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Without_a_server_there_is_nothing_to_pick() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();

        settings.OllamaPanel.Apply(Probe("llama3.2:1b"));

        Assert.False(settings.ModelPicker.IsEnabled);
        Assert.Equal("llama3.2:1b", Picked(settings)); // still shows what is configured
        Assert.Contains(L.AiModelSet, Picks(settings)[0].Note);
        Assert.Equal(Visibility.Visible, settings.AiModelNoneText.Visibility);
        Assert.False(settings.SaveModelButton.IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_configured_model_not_pulled_yet_says_so() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();

        settings.OllamaPanel.Apply(new OllamaProbe(
            OllamaReadiness.ModelMissing, "llama3.2:1b", "http://127.0.0.1:11434", null, ["qwen2.5:3b"]));

        Assert.True(settings.ModelPicker.IsEnabled);
        Assert.Equal(new[] { "llama3.2:1b", "qwen2.5:3b" }, Picks(settings).Select(p => p.Name));
        Assert.Contains(L.AiModelSetNotPulled, Picks(settings)[0].Note);
        Assert.Equal("llama3.2:1b", Picked(settings));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Hiding_the_window_forgets_an_unsaved_pick() => Wpf.Run(() =>
    {
        var settings = new SettingsPanel();
        settings.OllamaPanel.Apply(Probe("llama3.2:1b", "llama3.2:1b", "qwen2.5:3b"));
        settings.ModelPicker.SelectedIndex = 1;

        settings.Discard();
        Assert.Null(settings.ModelPicker.SelectedItem);
        Assert.False(settings.SaveModelButton.IsEnabled);

        settings.OllamaPanel.Apply(Probe("llama3.2:1b", "llama3.2:1b", "qwen2.5:3b"));
        Assert.Equal("llama3.2:1b", Picked(settings));
        return Task.CompletedTask;
    });

    private static OllamaProbe Probe(string model, params string[] installed) =>
        new(installed.Length > 0 ? OllamaReadiness.Ready : OllamaReadiness.Unreachable,
            model, "http://127.0.0.1:11434", null, installed);

    private static List<ModelPick> Picks(SettingsPanel settings) =>
        settings.ModelPicker.Items.Cast<ModelPick>().ToList();

    private static string? Picked(SettingsPanel settings) => (settings.ModelPicker.SelectedItem as ModelPick)?.Name;
}
