using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TheJukeBox.Infrastructure.Persistence;

/// <summary>
/// The one place that knows how this application talks to SQLite.
/// </summary>
/// <remarks>
/// Exposed as a single extension method so the tests configure their context through
/// exactly the same code path the application uses. A test that built its own
/// options -- or used an in-memory database -- would be exercising a configuration
/// that never ships, and would pass while WAL, the busy timeout, or foreign-key
/// enforcement were all silently absent in production.
/// </remarks>
public static class JukeBoxSqlite
{
    /// <summary>Builds a connection string for a database file.</summary>
    /// <remarks>
    /// Built rather than interpolated: a data path containing a semicolon or a quote
    /// (<c>C:\Users\O'Neill\...</c> is entirely ordinary on Windows) would otherwise
    /// produce a malformed connection string or silently drop keywords.
    /// </remarks>
    public static string ConnectionString(string databaseFile) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,

            // Create the file if absent, but never fall back to an in-memory or
            // temporary database: silently starting with an empty library is worse
            // than refusing to start.
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

    /// <summary>
    /// Points a context at <paramref name="databaseFile"/> with this instance's
    /// pragmas attached.
    /// </summary>
    public static DbContextOptionsBuilder UseJukeBoxSqlite(
        this DbContextOptionsBuilder builder,
        string databaseFile)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .UseSqlite(ConnectionString(databaseFile))
            .AddInterceptors(SqlitePragmaInterceptor.Instance);
    }
}
