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
        var dbPathSetting = config["agent:databasePath"] ?? "%ProgramData%/LocalOpsBot/data/localops.db";
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

        return services;
    }
}
