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
// installer writes to, then let LOCALOPSBOT__ environment variables override.
builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false)
    .AddJsonFile(@"C:\ProgramData\LocalOpsBot\config\appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("LOCALOPSBOT__");

// --- File logging (diagnostics) --------------------------------------------
// Serilog.Sinks.File is already referenced but was never initialized, so no
// log file was produced. Write daily rolling logs to ProgramData\logs.
var logDir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\LocalOpsBot\logs");
Directory.CreateDirectory(logDir);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(logDir, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
builder.Services.AddSerilog(Log.Logger, dispose: true);

builder.Services.AddWindowsService(options =>
{
    // Must match the service name the installer registers via sc.exe.
    options.ServiceName = "LocalOpsBot.Agent";
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
