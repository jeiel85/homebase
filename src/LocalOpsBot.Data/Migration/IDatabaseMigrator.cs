namespace LocalOpsBot.Data.Migration;

public interface IDatabaseMigrator
{
    Task MigrateAsync(CancellationToken ct);
}
