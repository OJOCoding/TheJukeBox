using Microsoft.EntityFrameworkCore;

namespace TheJukeBox.Infrastructure.Persistence;

/// <summary>
/// Takes a consistent copy of the database before a schema change.
/// </summary>
/// <remarks>
/// <para>
/// A migration is the one routine operation that can destroy a self-hoster's data
/// with no way back: <c>ALTER TABLE</c> on SQLite is often a table rebuild, and a bug
/// in a future migration -- or a power cut halfway through one -- leaves a database
/// that neither the old nor the new build can open. Nobody backs up before running
/// <c>docker compose pull</c>, so the upgrade has to do it.
/// </para>
/// <para>
/// <c>VACUUM INTO</c> rather than copying the file: it takes a read transaction for
/// the duration, so the copy is a transactionally consistent snapshot with the WAL
/// already folded in. Copying <c>jukebox.db</c> while a writer is active would
/// produce a torn file, and copying it without <c>jukebox.db-wal</c> would silently
/// lose every committed-but-not-yet-checkpointed transaction.
/// </para>
/// </remarks>
public static class DatabaseBackup
{
    /// <summary>The directory name, under the data path, holding backups.</summary>
    public const string DirectoryName = "backups";

    private const string FilePrefix = "jukebox-";
    private const string FileExtension = ".db";

    /// <summary>Backups live beside the database, under the data path.</summary>
    public static string DirectoryFor(string dataPath) => Path.Combine(dataPath, DirectoryName);

    /// <summary>
    /// Writes a consistent snapshot of <paramref name="context"/>'s database into
    /// <paramref name="backupsDirectory"/> and returns it.
    /// </summary>
    public static async Task<FileInfo> CreateAsync(
        JukeBoxDbContext context,
        string backupsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        Directory.CreateDirectory(backupsDirectory);

        // Second precision would collide if an operator restarted twice within the
        // same second while a migration was pending; VACUUM INTO refuses to overwrite
        // an existing file, so a collision would turn a retry into a hard failure.
        var target = Path.Combine(
            backupsDirectory,
            $"{FilePrefix}{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'.'fff'Z'}{FileExtension}");

        await context.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var connection = context.Database.GetDbConnection();

            await using var command = connection.CreateCommand();

            // The destination is a bound parameter, not interpolated text. The value
            // is ours either way, but a literal would need SQL quote-doubling to
            // survive an apostrophe in the user's profile path.
            command.CommandText = "VACUUM INTO $target;";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "$target";
            parameter.Value = target;
            command.Parameters.Add(parameter);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        return new FileInfo(target);
    }

    /// <summary>
    /// Deletes all but the <paramref name="keep"/> most recent backups and returns how
    /// many were removed.
    /// </summary>
    /// <remarks>
    /// Ordered by the timestamp in the file name rather than by
    /// <c>LastWriteTime</c>: restoring a backup, or copying the data directory to a
    /// new host, rewrites file times and would otherwise make the retention window
    /// delete the wrong files.
    /// </remarks>
    public static int Prune(string backupsDirectory, int keep)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);

        if (!Directory.Exists(backupsDirectory))
        {
            return 0;
        }

        var stale = Directory
            .EnumerateFiles(backupsDirectory, $"{FilePrefix}*{FileExtension}")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(keep)
            .ToArray();

        var deleted = 0;

        foreach (var file in stale)
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Retention is housekeeping. Failing the upgrade because an old
                // backup could not be deleted would be strictly worse than keeping
                // one file too many.
            }
        }

        return deleted;
    }
}
