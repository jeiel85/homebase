using LocalOpsBot.Data.Migration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

public sealed class DatabaseMigrationService : IHostedService
{
    private readonly IDatabaseMigrator _migrator;
    private readonly ILogger<DatabaseMigrationService> _logger;

    public DatabaseMigrationService(IDatabaseMigrator migrator, ILogger<DatabaseMigrationService> logger)
    {
        _migrator = migrator;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Running database migrations...");
        await _migrator.MigrateAsync(ct);
        _logger.LogInformation("Database migrations complete");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
