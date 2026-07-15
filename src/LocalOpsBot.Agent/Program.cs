using System.Globalization;
using System.Runtime.Versioning;
using LocalOpsBot.Agent.Services;
using LocalOpsBot.Core;
using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Core.Notifications;
using LocalOpsBot.Data;
using LocalOpsBot.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

[assembly: SupportedOSPlatform("windows")]

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// --- Configuration sources -------------------------------------------------
// The Windows service starts with CWD = C:\Windows\System32, so the default
// CWD-relative appsettings.json is never found. Load by absolute path from the
// executable's own folder and from the machine-wide ProgramData location the
// installer writes to, then let HOMEBASE__ environment variables override.
builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false)
    .AddJsonFile(@"C:\ProgramData\Homebase\config\appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("HOMEBASE__");

// --- File logging (diagnostics) --------------------------------------------
// Serilog.Sinks.File is already referenced but was never initialized, so no
// log file was produced. Write daily rolling logs to ProgramData\logs.
var logDir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\Homebase\logs");
Directory.CreateDirectory(logDir);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // HttpClient request logging emits the full URL at Information — for the Telegram client that
    // URL embeds the bot token. Drop these to Warning so the secret never lands in the log file.
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(logDir, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// Single-instance guard. The SCM already keeps the *service* single, but a second instance
// launched directly (e.g. from a console) would double-poll Telegram and duplicate every alert.
// Global\ so it spans sessions; refuse to start if another Agent already holds it.
using var singleInstance = new Mutex(initiallyOwned: false, @"Global\Homebase.Agent.SingleInstance", out bool agentCreatedNew);
if (!agentCreatedNew)
{
    Log.Warning("Another Homebase.Agent instance is already running; this instance will exit.");
    Log.CloseAndFlush();
    return;
}

builder.Services.AddSerilog(Log.Logger, dispose: true);

// --- Bot response language -------------------------------------------------
// Localize user-facing bot text: use agent:language (a culture code like "ko" or "en"), or the
// OS display language when empty. Set app-wide so background tasks reply in the same language.
// Runs after Serilog init so an invalid code is logged rather than silently ignored.
var configuredLanguage = builder.Configuration["agent:language"];
try
{
    var culture = string.IsNullOrWhiteSpace(configuredLanguage)
        ? CultureInfo.InstalledUICulture
        : CultureInfo.GetCultureInfo(configuredLanguage);
    CultureInfo.DefaultThreadCurrentCulture = culture;
    CultureInfo.DefaultThreadCurrentUICulture = culture;
}
catch (CultureNotFoundException)
{
    // Unknown code: fall back to the OS display language and tell the user their setting was ignored.
    CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InstalledUICulture;
    CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InstalledUICulture;
    Log.Warning(
        "Configured agent:language '{Language}' is not a valid culture code (use e.g. \"ko\" or \"en\"); " +
        "falling back to the OS display language.", configuredLanguage);
}

builder.Services.AddWindowsService(options =>
{
    // Must match the service name the installer registers via sc.exe.
    options.ServiceName = "Homebase.Agent";
});

builder.Services.AddLocalOpsCore(builder.Configuration);
builder.Services.AddLocalOpsTelegram(builder.Configuration);
builder.Services.AddLocalOpsLlm();
builder.Services.AddLocalOpsWindowsCollectors();
builder.Services.AddLocalOpsData(builder.Configuration);
builder.Services.AddSingleton<IAlertDispatcher, AlertDispatcher>();
builder.Services.AddHostedService<DatabaseMigrationService>();
builder.Services.AddHostedService<TelegramPollingService>();
builder.Services.AddHostedService<BootNotificationService>();
builder.Services.AddHostedService<WatchdogBackgroundService>();
builder.Services.AddHostedService<EventLogPollingService>();
builder.Services.AddHostedService<DevMonitorBackgroundService>();
builder.Services.AddHostedService<PcHealthAdvisorService>();
builder.Services.AddLocalOpsDevMonitor();
builder.Services.AddLocalOpsUpdates();

// --- Toast notification forwarding (opt-in) --------------------------------
// Only wire the pipe server + forwarder when the feature is enabled; otherwise
// their dependencies (ITextMasker / INotificationBridgeServer) are not
// registered and resolving these hosted services would crash the host at start.
builder.Services.AddLocalOpsNotificationForwarding(builder.Configuration);
var forwardingEnabled = builder.Configuration.GetSection("notificationForwarding").GetValue<bool>("enabled");
if (forwardingEnabled)
{
    builder.Services.AddSingleton<NotificationBridgeServer>();
    builder.Services.AddSingleton<INotificationBridgeServer>(sp => sp.GetRequiredService<NotificationBridgeServer>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationBridgeServer>());
    builder.Services.AddHostedService<NotificationForwardingService>();
}

try
{
    IHost host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Homebase Agent terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
