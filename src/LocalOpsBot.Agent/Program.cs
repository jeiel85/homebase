using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LocalOpsBot Agent";
});

// TODO GOAL-00/01:
// builder.Services.AddLocalOpsCore(builder.Configuration);
// builder.Services.AddLocalOpsTelegram(builder.Configuration);
// builder.Services.AddLocalOpsData(builder.Configuration);
// builder.Services.AddHostedService<TelegramPollingService>();
// builder.Services.AddHostedService<BootNotificationService>();

IHost host = builder.Build();
await host.RunAsync();
