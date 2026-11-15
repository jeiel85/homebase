using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using LocalOpsBot.Data;
using LocalOpsBot.Data.Migration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Agent.Services;

[SupportedOSPlatform("windows")]
public sealed class DatabaseMigrationService : IHostedService
{
    private readonly IDatabaseMigrator _migrator;
    private readonly LocalOpsDbContext _db;
    private readonly ILogger<DatabaseMigrationService> _logger;

    public DatabaseMigrationService(IDatabaseMigrator migrator, LocalOpsDbContext db, ILogger<DatabaseMigrationService> logger)
    {
        _migrator = migrator;
        _db = db;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        RestrictDataDirectory();
        _logger.LogInformation("Running database migrations...");
        await _migrator.MigrateAsync(ct);
        _logger.LogInformation("Database migrations complete");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// The database holds relayed notification text (DECISIONS M-32), so its folder is limited to
    /// SYSTEM and Administrators — ProgramData otherwise lets every local user read it. Only the
    /// service (LocalSystem) does this: a developer run as a normal user would lock itself out.
    /// </summary>
    private void RestrictDataDirectory()
    {
        var dir = _db.FilePath is { } path ? Path.GetDirectoryName(path) : null;
        if (dir is null || !Directory.Exists(dir)) return;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!identity.IsSystem) return;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            // Applied by name, so the inherited entries of the files already inside are replaced too.
            new DirectoryInfo(dir).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            _logger.LogWarning(ex, "Could not limit access to the data folder {Directory}", dir);
        }
    }
}
