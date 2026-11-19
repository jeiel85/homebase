using System.Text.Json.Nodes;

namespace LocalOpsBot.Tray.Services;

internal enum ModelChangeCheck { Ok, Invalid, Unchanged }

/// <summary>
/// Changes the model AI advice uses (<c>llmAdvisor.model</c>) in the Agent config via
/// <see cref="ElevatedConfig"/>: one administrator approval, then the Agent restarts and reads it.
/// Only that one value is touched; the rest of the file is written back as it was read.
/// </summary>
internal static class LlmAdvisorConfigurator
{
    private const string Section = "llmAdvisor";
    private const string ModelKey = "model";

    /// <summary>Whether <paramref name="input"/> is worth asking for approval to save.</summary>
    public static ModelChangeCheck Check(string input, string current)
    {
        var model = input.Trim();
        if (!OllamaSetup.IsValidModelName(model)) return ModelChangeCheck.Invalid;
        return string.Equals(model, current, StringComparison.Ordinal) ? ModelChangeCheck.Unchanged : ModelChangeCheck.Ok;
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

    internal static void SetModel(JsonObject root, string model)
    {
        var sectionKey = KeyOf(root, Section);
        if (root[sectionKey] is not JsonObject section)
        {
            section = new JsonObject();
            root[sectionKey] = section;
        }
        section[KeyOf(section, ModelKey)] = model;
    }

    // The Agent binds config without regard to case, and its JSON reader refuses two keys that differ
    // only in case — so a file that spells it "LlmAdvisor"/"Model" is written under that spelling
    // instead of gaining a second key that would stop the Agent from reading its config at all.
    private static string KeyOf(JsonObject obj, string name) =>
        obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
}
