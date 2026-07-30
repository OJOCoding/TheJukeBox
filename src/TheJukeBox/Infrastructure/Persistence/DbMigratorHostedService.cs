using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheJukeBox.Configuration;
using TheJukeBox.Diagnostics;

namespace TheJukeBox.Infrastructure.Persistence;

/// <summary>
/// Brings the database schema up to date before the application serves traffic.
/// </summary>
/// <remarks>
/// Uses <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/> and never
/// <c>EnsureCreatedAsync</c>. <c>EnsureCreated</c> builds the schema directly from the
/// current model and writes no migration-history rows, so the resulting database looks
/// to EF like a database at migration zero with tables that already exist: the first
/// real migration then fails on "table already exists" and there is no supported way
/// forward. It is convenient exactly until the second schema change.
/// </remarks>
internal sealed class DbMigratorHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<StorageOptions> storageOptions,
    JukeBoxPaths paths,
    ILogger<DbMigratorHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Captured before the first connection: opening the context creates the file,
        // after which "did this database already exist?" is unanswerable, and that is
        // what decides whether a backup is meaningful.
        var databaseExisted = File.Exists(paths.DatabaseFile);

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<JukeBoxDbContext>();

        try
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

            if (pending.Length == 0)
            {
                Log.DatabaseUpToDate(logger, paths.DatabaseFile);
                return;
            }

            if (!storageOptions.Value.AutoMigrate)
            {
                Log.MigrationsPendingButDisabled(logger, pending.Length);
                return;
            }

            if (databaseExisted)
            {
                await BackUpAsync(context, cancellationToken);
            }

            var names = string.Join(", ", pending);

            Log.ApplyingMigrations(logger, pending.Length, names);

            await context.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // An unusable database is a configuration problem far more often than a
            // code one -- bind-mount ownership, a full disk, a read-only volume -- so
            // it gets the same treatment as missing credentials: an explanation and a
            // non-zero exit, not a stack trace buried in host startup noise.
            StartupValidation.PrintStorageFailureAndExit(e, paths.DataPath);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task BackUpAsync(JukeBoxDbContext context, CancellationToken cancellationToken)
    {
        var keep = storageOptions.Value.BackupsToKeep;

        if (keep <= 0)
        {
            return;
        }

        var backupsDirectory = DatabaseBackup.DirectoryFor(paths.DataPath);

        try
        {
            var backup = await DatabaseBackup.CreateAsync(context, backupsDirectory, cancellationToken);

            Log.DatabaseBackedUp(logger, backup.FullName, backup.Length);

            var pruned = DatabaseBackup.Prune(backupsDirectory, keep);

            if (pruned > 0)
            {
                Log.BackupsPruned(logger, pruned);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Deliberately not fatal. Refusing to start because the safety net could
            // not be spread would strand the user on a version that may itself be the
            // problem; a loud warning plus a working app is the better trade.
            Log.DatabaseBackupFailed(logger, e);
        }
    }
}
