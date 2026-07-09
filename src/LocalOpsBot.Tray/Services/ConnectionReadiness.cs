using System.Net.Http;
using System.ServiceProcess;
using LocalOpsBot.Infrastructure.Llm;
using LocalOpsBot.Infrastructure.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LocalOpsBot.Tray.Services;

internal enum AgentServiceState { NotInstalled, Stopped, Running }

internal sealed record ReadinessSnapshot(
    AgentServiceState Service,
    bool TokenConfigured,
    bool ChatConfigured,
    long? PrimaryChatId);

internal sealed record TestSendResult(bool Ok, string Message);

internal sealed record OllamaProbe(
    OllamaReadiness Status, string Model, string Endpoint, string? Detail);

/// <summary>
/// Read-only view of whether this machine is ready to talk to Telegram, plus a one-shot
/// test send. Everything here works without elevation: the Agent service status comes from
/// <see cref="ServiceController"/>, the chat allowlist from the world-readable ProgramData
/// config the installer writes, and the bot token from the machine-level environment
/// variable the installer sets. Nothing is written back — onboarding only guides and probes.
/// </summary>
internal sealed class ConnectionReadiness
{
    private const string ServiceName = "Homebase.Agent";
    private const string ConfigPath  = @"C:\ProgramData\Homebase\config\appsettings.json";
    private const string TokenEnvVar = "HOMEBASE_TELEGRAM_TOKEN";

    public ReadinessSnapshot Probe()
    {
        var config = LoadConfig();
        var token  = ResolveToken(config);
        var chatId = ReadPrimaryChatId(config);
        return new ReadinessSnapshot(
            Service: ReadServiceState(),
            TokenConfigured: !string.IsNullOrWhiteSpace(token),
            ChatConfigured: chatId is not null,
            PrimaryChatId: chatId);
    }

    public async Task<TestSendResult> SendTestMessageAsync(CancellationToken ct)
    {
        var config = LoadConfig();
        var token  = ResolveToken(config);
        var chatId = ReadPrimaryChatId(config);

        if (string.IsNullOrWhiteSpace(token))
            return new TestSendResult(false, "Bot token is not configured yet.");
        if (chatId is null)
            return new TestSendResult(false, "No Telegram chat ID is configured yet.");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // Pass the already-resolved token as a literal so the send uses the freshly-read
            // machine value regardless of whether this process inherited the env var at start.
            var options = Options.Create(new TelegramOptions { BotToken = token });
            var client  = new TelegramClient(http, options);
            await client.SendMessageAsync(
                chatId.Value,
                "✅ Homebase test — your PC is connected to Telegram.",
                new TelegramSendOptions(ParseMode: null), // plain text: no HTML entity pitfalls
                ct);
            return new TestSendResult(true, $"Sent to chat {chatId.Value}. Check Telegram.");
        }
        catch (Exception ex)
        {
            // Defensive: the bot token is embedded in the Telegram request URL, so scrub it
            // from any exception text before it reaches the UI.
            return new TestSendResult(false, $"Send failed: {Redact(ex.Message, token)}");
        }
    }

    /// <summary>
    /// Checks whether the local Ollama server (from the same config the Agent reads) is running
    /// and has the configured model pulled, so onboarding can guide the user through AI setup.
    /// </summary>
    public async Task<OllamaProbe> ProbeOllamaAsync(CancellationToken ct)
    {
        var config = LoadConfig();
        var endpoint = config["llmAdvisor:endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = "http://127.0.0.1:11434";
        var model = config["llmAdvisor:model"];
        if (string.IsNullOrWhiteSpace(model)) model = "llama3.2:1b";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var result = await new OllamaReadinessChecker(http).CheckAsync(endpoint, model, ct);
        return new OllamaProbe(result.Status, model, endpoint, result.Detail);
    }

    private static string Redact(string message, string token) =>
        string.IsNullOrEmpty(token) ? message : message.Replace(token, "***");

    // Mirror the Agent's config sources: the installer-written ProgramData file, then
    // HOMEBASE__ env overrides. Rebuilt per call so it reflects the current file state.
    private static IConfigurationRoot LoadConfig() =>
        new ConfigurationBuilder()
            .AddJsonFile(ConfigPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("HOMEBASE__")
            .Build();

    // Mirrors TelegramClient's "ENV:VARNAME" indirection so the secret stays out of the
    // config file. Reads the machine-level env var explicitly (allowed without elevation)
    // so a token the installer set after this process launched is still seen.
    private static string? ResolveToken(IConfiguration config)
    {
        var raw = config["telegram:botToken"];
        if (string.IsNullOrEmpty(raw))
            return Environment.GetEnvironmentVariable(TokenEnvVar, EnvironmentVariableTarget.Machine);
        if (raw.StartsWith("ENV:", StringComparison.Ordinal))
        {
            var name = raw[4..];
            return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
                   ?? Environment.GetEnvironmentVariable(name);
        }
        return raw;
    }

    private static long? ReadPrimaryChatId(IConfiguration config)
    {
        foreach (var child in config.GetSection("telegram:allowedChatIds").GetChildren())
            if (long.TryParse(child.Value, out var id))
                return id;
        return null;
    }

    private static AgentServiceState ReadServiceState()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status == ServiceControllerStatus.Running // throws if not installed
                ? AgentServiceState.Running
                : AgentServiceState.Stopped;
        }
        catch
        {
            return AgentServiceState.NotInstalled;
        }
    }
}
