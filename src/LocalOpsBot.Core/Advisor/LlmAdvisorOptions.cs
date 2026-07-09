namespace LocalOpsBot.Core.Advisor;

/// <summary>
/// Configuration for the local-LLM PC health advisor. Talks to a local Ollama/LM Studio
/// server over HTTP — no model is bundled, so this stays lightweight and reuses whatever the
/// user already runs. Bound from the "llmAdvisor" config section.
/// </summary>
public sealed class LlmAdvisorOptions
{
    /// <summary>Master switch. When false, /advise reports that the advisor is disabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Base URL of the local LLM server (Ollama default shown).</summary>
    public string Endpoint { get; init; } = "http://127.0.0.1:11434";

    /// <summary>Model to prompt — must already be pulled on the server (e.g. `ollama pull llama3.2:1b`).</summary>
    public string Model { get; init; } = "llama3.2:1b";

    /// <summary>Per-request timeout. Small local models are usually well under this.</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Language for the advice reply, e.g. "Korean" or "English". Empty means the OS display
    /// language (so a Korean Windows gets Korean advice without configuration).
    /// </summary>
    public string Language { get; init; } = "";

    /// <summary>
    /// Cap on generated tokens (Ollama <c>num_predict</c>). Lower is faster and keeps advice short;
    /// 350 fits a few bullet points.
    /// </summary>
    public int MaxTokens { get; init; } = 350;

    /// <summary>
    /// Keep the model loaded in memory this many minutes between calls (Ollama <c>keep_alive</c>),
    /// so a second request doesn't pay the model-load cost again. 0 unloads immediately.
    /// </summary>
    public int KeepAliveMinutes { get; init; } = 10;
}
