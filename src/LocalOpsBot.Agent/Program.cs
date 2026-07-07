using System.Runtime.Versioning;
using LocalOpsBot.Agent.Services;
using LocalOpsBot.Core;
using LocalOpsBot.Data;
using LocalOpsBot.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[assembly: SupportedOSPlatform("windows")]

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LocalOpsBot Agent";
});

builder.Services.AddLocalOpsCore(builder.Configuration);
builder.Services.AddLocalOpsTelegram(builder.Configuration);
builder.Services.AddLocalOpsWindowsCollectors();
builder.Services.AddLocalOpsData(builder.Configuration);
builder.Services.AddHostedService<DatabaseMigrationService>();
builder.Services.AddHostedService<TelegramPollingService>();
builder.Services.AddHostedService<BootNotificationService>();
builder.Services.AddHostedService<WatchdogBackgroundService>();
builder.Services.AddHostedService<EventLogPollingService>();

IHost host = builder.Build();
await host.RunAsync();
