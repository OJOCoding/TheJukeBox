using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TheJukeBox.Infrastructure.Persistence;

/// <summary>
/// Applies this instance's required SQLite pragmas to every connection as it opens.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why per-connection and not once at startup.</strong> <c>PRAGMA</c> is
/// almost always connection-scoped, not database-scoped: <c>busy_timeout</c> and
/// <c>foreign_keys</c> are properties of the open connection and reset to their
/// defaults on the next one. <c>Microsoft.Data.Sqlite</c> pools connections, and EF
/// Core opens and returns one per operation, so a one-shot at startup would have
/// configured exactly one connection -- the one used at startup -- and every request
/// afterwards would run with a 0&#160;ms busy timeout and, potentially, foreign keys
/// off. Worse, it would appear to work: the startup connection is the one the
/// migrator uses, so migrations would succeed and the missing enforcement would only
/// surface later as orphaned rows nobody can explain.
/// </para>
/// <para>
/// <c>journal_mode</c> is the exception -- it is recorded in the database header and
/// survives -- but setting all three in one place is cheaper than documenting which
/// of them is special, and re-issuing it is a no-op that returns the current mode.
/// </para>
/// <para>
/// Ordering matters. <c>busy_timeout</c> is set first, because switching
/// <c>journal_mode</c> needs a brief exclusive lock and would otherwise throw
/// <c>SQLITE_BUSY</c> immediately if another connection happened to hold a read
/// transaction at that moment.
/// </para>
/// </remarks>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// How long SQLite waits for a lock before returning <c>SQLITE_BUSY</c>.
    /// </summary>
    /// <remarks>
    /// SQLite's default is 0: fail instantly. With WAL a reader never blocks, but two
    /// writers still serialise, and the sync worker writing batches while a user
    /// saves playback progress is exactly that case. Five seconds is far longer than
    /// any batch here needs and turns a contended write from an error into a pause.
    /// </remarks>
    public const int BusyTimeoutMilliseconds = 5000;

    /// <summary>Shared because the interceptor holds no state.</summary>
    public static SqlitePragmaInterceptor Instance { get; } = new();

    private static readonly string[] Pragmas =
    [
        $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};",

        // SQLite does not enforce foreign keys unless asked, per connection. Without
        // this, ON DELETE CASCADE is inert and deleting a user leaves their token and
        // sync state behind as unreachable rows.
        "PRAGMA foreign_keys = ON;",

        // Write-ahead logging: readers and one writer proceed concurrently. Without
        // it the background sync writer holds an exclusive lock for the duration of
        // each batch and every concurrent read -- i.e. anyone using the app -- blocks
        // behind it.
        "PRAGMA journal_mode = WAL;",

        // With WAL, NORMAL means a commit does not fsync; the WAL is fsynced at
        // checkpoint instead. The exposure is losing the most recent transactions on
        // an OS crash or power loss, not corruption. For a music library that is a
        // few re-synced tracks, and it is worth an order of magnitude on write
        // throughput during the initial sync.
        "PRAGMA synchronous = NORMAL;",
    ];

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();

        foreach (var pragma in Pragmas)
        {
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        foreach (var pragma in Pragmas)
        {
            command.CommandText = pragma;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
