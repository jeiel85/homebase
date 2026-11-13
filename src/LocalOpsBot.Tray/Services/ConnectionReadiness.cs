using System.IO;
using System.Net.Http;
using System.Text.Json;
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

    /// <summary>
    /// Sends a test notification. When the Agent is reachable over the mobile bridge, it goes through
    /// the Agent's router like a real alert — phone push first, Telegram as the fallback (DECISIONS M-19).
    /// Otherwise (Agent not running yet during setup, or the mobile feature off) it falls back to a
    /// direct Telegram send so the setup wizard can still verify the bot.
    /// </summary>
    public async Task<TestSendResult> SendTestMessageAsync(CancellationToken ct)
    {
        if (Mobile.MobileHost.Client is { IsConnected: true } bridge)
        {
            try
            {
                var response = await bridge.RequestAsync(LocalOpsBot.Core.Mobile.MobileBridge.OpNotifyTest, ct: ct);
                var result = response["payload"]?.Deserialize<LocalOpsBot.Core.Mobile.NotifyTestResult>(
                    LocalOpsBot.Core.Mobile.MobileProtocol.Json);
                if (result is { Delivered: true })
                    return new TestSendResult(true, L.TestSentVia(result.Channel ?? "?"));
                if (result is not null)
                    return new TestSendResult(false, L.TestFailed(result.Error ?? "?"));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException)
            {
                // Bridge dropped mid-request: fall through to the direct Telegram check.
            }
        }

        return await SendTelegramTestAsync(ct);
    }

    private async Task<TestSendResult> SendTelegramTestAsync(CancellationToken ct)
    {
        var config = LoadConfig();
        var token  = ResolveToken(config);
        var chatId = ReadPrimaryChatId(config);

        if (string.IsNullOrWhiteSpace(token))
            return new TestSendResult(false, L.TestNoToken);
        if (chatId is null)
            return new TestSendResult(false, L.TestNoChat);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // Pass the already-resolved token as a literal so the send uses the freshly-read
            // machine value regardless of whether this process inherited the env var at start.
            var options = Options.Create(new TelegramOptions { BotToken = token });
            var client  = new TelegramClient(http, options);
            await client.SendMessageAsync(
                chatId.Value,
                L.TestMessageBody,
                new TelegramSendOptions(ParseMode: null), // plain text: no HTML entity pitfalls
                ct);
            return new TestSendResult(true, L.TestSent(chatId.Value));
        }
        catch (Exception ex)
        {
            // Defensive: the bot token is embedded in the Telegram request URL, so scrub it
            // from any exception text before it reaches the UI.
            return new TestSendResult(false, L.TestFailed(Redact(ex.Message, token)));
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

    // Same config sources the Agent reads (ProgramData JSON + HOMEBASE__ env), via the shared
    // loader. Rebuilt per call so it reflects the current file state.
    private static IConfigurationRoot LoadConfig() => TrayConfig.Load();

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
