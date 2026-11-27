using System.Text.Json.Nodes;

namespace LocalOpsBot.Tray.Services;

internal enum ModelChangeCheck { Ok, Invalid, Unchanged }

/// <summary>
/// What the model picker in Settings › AI advice offers: <see cref="Models"/> in list order, and
/// <see cref="Current"/>, the entry that is the configured model. <see cref="CurrentPulled"/> is false
/// when the server didn't report the configured model (not pulled yet, or the server wasn't reached).
/// </summary>
internal sealed record ModelChoices(IReadOnlyList<string> Models, string Current, bool CurrentPulled);

/// <summary>
/// Changes the model AI advice uses (<c>llmAdvisor.model</c>) in the Agent config via
/// <see cref="ElevatedConfig"/>: one administrator approval, then the Agent restarts and reads it.
/// Only that one value is touched; the rest of the file is written back as it was read.
/// </summary>
internal static class LlmAdvisorConfigurator
{
    private const string Section = "llmAdvisor";
    private const string ModelKey = "model";

    /// <summary>
    /// Whether <paramref name="input"/> is worth asking for approval to save. The picker only offers what
    /// the server reports, but the name still reaches the config and the pull command, so it is checked.
    /// </summary>
    public static ModelChangeCheck Check(string input, string current)
    {
        var model = input.Trim();
        if (!OllamaSetup.IsValidModelName(model)) return ModelChangeCheck.Invalid;
        // Ollama matches model names without regard to case, so a case-only edit selects the same model.
        return string.Equals(model, current, StringComparison.OrdinalIgnoreCase) ? ModelChangeCheck.Unchanged : ModelChangeCheck.Ok;
    }

    /// <summary>
    /// The picker's entries: the models the server has pulled, by name. The configured model is the pulled
    /// one Ollama would load for it, so picking that entry is no change. Missing configured models
    /// are described by the setup row, never offered as installed choices.
    /// </summary>
    public static ModelChoices ChoicesFor(string configured, IReadOnlyList<string> pulled)
    {
        var models = pulled
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // The Agent sends the configured name as it is, and Ollama loads a name without a tag as its
        // ":latest" tag — so a bare "llama3.2" is "llama3.2:latest", never another pulled tag of it.
        var loaded = configured.Contains(':') ? configured : configured + ":latest";
        var match = models.FirstOrDefault(m =>
            string.Equals(m, loaded, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m, configured, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return new ModelChoices(models, match, CurrentPulled: true);
        return new ModelChoices(models, configured, CurrentPulled: false);
    }

    /// <summary>
    /// The machine-level <c>HOMEBASE__llmAdvisor__model</c>, which the Agent (a service) reads over the
    /// config file — or null. The Tray's own environment isn't asked: it was inherited when the Tray
    /// started and can include user-level variables the service never sees.
    /// </summary>
    public static string? ServiceOverride()
    {
        var value = Environment.GetEnvironmentVariable("HOMEBASE__llmAdvisor__model", EnvironmentVariableTarget.Machine);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Writes <paramref name="model"/> as <c>llmAdvisor.model</c>. Returns <c>false</c> when it was not
    /// saved (administrator approval declined, or the elevated step failed).
    /// </summary>
    public static async Task<bool> SaveModelAsync(string model)
    {
        if (!OllamaSetup.IsValidModelName(model))
            throw new ArgumentException("Not a valid Ollama model name.", nameof(model));
        var root = await ElevatedConfig.ReadAsync();
        SetModel(root, model);
        return await ElevatedConfig.WriteAsync(root);
    }

    internal static void SetModel(JsonObject root, string model) =>
        ElevatedConfig.SetValue(root, Section, ModelKey, model);
}
