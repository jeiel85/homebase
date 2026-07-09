using LocalOpsBot.Core.Alerts;
using LocalOpsBot.Data.Adapters;
using LocalOpsBot.Data.Migration;
using LocalOpsBot.Data.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LocalOpsBot.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLocalOpsData(
        this IServiceCollection services, IConfiguration config)
    {
        var dbPathSetting = config["agent:databasePath"] ?? "%ProgramData%/Homebase/data/localops.db";
        var dataOpts = new DataOptions { DatabasePath = dbPathSetting };
        var dbPath = Environment.ExpandEnvironmentVariables(dataOpts.DatabasePath);
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var connectionString = $"Data Source={dbPath}";
        services.AddSingleton(new LocalOpsDbContext(connectionString));

        services.AddSingleton<IDatabaseMigrator, SqliteMigrator>();
        services.AddSingleton<IRuntimeStateRepository, RuntimeStateRepository>();
        services.AddSingleton<ICommandLogRepository, CommandLogRepository>();
        services.AddSingleton<IAlertLogRepository, AlertLogRepository>();
        services.AddSingleton<IMetricRepository, MetricRepository>();
        services.AddSingleton<INotificationEventRepository, NotificationEventRepository>();
        services.AddSingleton<IWatchStatusRepository, WatchStatusRepository>();

        services.AddSingleton<IStateStore, StateStoreAdapter>();
        services.AddSingleton<IAlertStore, AlertStoreAdapter>();

        return services;
    }
}
