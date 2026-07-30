using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TheJukeBox.Domain;
using TheJukeBox.Infrastructure.Persistence;
using TheJukeBox.Infrastructure.Security;

namespace TheJukeBox.Tests;

/// <summary>
/// A migrated database on a real temporary file, disposed with the class.
/// </summary>
/// <remarks>
/// Deliberately a file rather than <c>:memory:</c>. An in-memory SQLite database is
/// destroyed when its last connection closes, so using one forces you to pin a
/// connection open and defeat pooling -- at which point the tests are exercising a
/// configuration that never ships, and would pass while WAL, the busy timeout, and
/// foreign-key enforcement were all silently absent in production. A temp file costs
/// a couple of milliseconds and exercises the real thing.
/// </remarks>
public sealed class SqliteDatabase : IDisposable
{
    private readonly string _directory;

    public SqliteDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "jukebox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        DatabaseFile = Path.Combine(_directory, "jukebox.db");

        using var context = CreateContext();

        // Migrate, not EnsureCreated: this makes every test run a check that the
        // committed migration chain actually applies to an empty database.
        context.Database.Migrate();
    }

    public string DatabaseFile { get; }

    public JukeBoxDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<JukeBoxDbContext>();

        // Routed through the same extension method the application uses, so the
        // pragmas and connection-string handling under test are the shipped ones.
        builder.UseJukeBoxSqlite(DatabaseFile);

        return new JukeBoxDbContext(builder.Options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle should not fail an otherwise passing run; the temp
            // directory is reclaimed by the OS.
        }
    }
}

public class SchemaTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    /// <summary>
    /// Catches the "changed the model, forgot to add a migration" mistake. For a
    /// self-hosted app that omission means a broken upgrade for every user, and it is
    /// invisible locally because the developer's database was created from the model.
    /// </summary>
    [Fact]
    public void No_model_changes_are_missing_a_migration()
    {
        using var context = database.CreateContext();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void All_migrations_are_applied()
    {
        using var context = database.CreateContext();

        var applied = context.Database.GetAppliedMigrations().ToArray();

        Assert.NotEmpty(applied);
        Assert.Empty(context.Database.GetPendingMigrations());
    }
}

public class SqlitePragmaTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    [Theory]
    [InlineData("journal_mode", "wal")]
    [InlineData("foreign_keys", "1")]
    public void Pragma_is_applied_to_every_connection(string pragma, string expected)
    {
        using var context = database.CreateContext();

        Assert.Equal(expected, ScalarOf(context, $"PRAGMA {pragma};"), ignoreCase: true);
    }

    [Fact]
    public void Busy_timeout_is_set()
    {
        using var context = database.CreateContext();

        Assert.Equal(
            SqlitePragmaInterceptorProbe.ExpectedBusyTimeout.ToString(),
            ScalarOf(context, "PRAGMA busy_timeout;"));
    }

    /// <summary>
    /// SQLite does not enforce foreign keys unless asked, per connection. Asserting the
    /// pragma reads back as "1" proves it was set; only a rejected insert proves it is
    /// actually being enforced.
    /// </summary>
    [Fact]
    public async Task Foreign_keys_are_enforced_not_merely_enabled()
    {
        using var context = database.CreateContext();

        context.DriveTokens.Add(new DriveToken
        {
            UserId = Guid.CreateVersion7(),
            EncryptedRefreshToken = "irrelevant",
            Scopes = "drive.readonly",
            GrantedAtUtc = DateTimeOffset.UtcNow,
        });

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.IsType<SqliteException>(thrown.InnerException);
    }

    /// <summary>
    /// The reason SQLitePCLRaw.lib.e_sqlite3 is pinned in Directory.Packages.props:
    /// CVE-2025-6965 is a memory-corruption flaw in SQLite below 3.50.2, reachable
    /// through ordinary aggregate queries. A package-version pin only claims to fix
    /// it; this asserts the engine actually loaded at runtime is patched.
    /// </summary>
    [Fact]
    public void Loaded_sqlite_engine_is_not_vulnerable_to_cve_2025_6965()
    {
        using var context = database.CreateContext();

        var reported = ScalarOf(context, "select sqlite_version();");

        Assert.NotNull(reported);
        Assert.True(
            Version.Parse(reported) >= new Version(3, 50, 2),
            $"SQLite {reported} is affected by CVE-2025-6965; 3.50.2 or later is required.");
    }

    private static string? ScalarOf(JukeBoxDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return command.ExecuteScalar()?.ToString();
    }

    private static class SqlitePragmaInterceptorProbe
    {
        public const int ExpectedBusyTimeout = 5000;
    }
}

public class EntityMappingTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    [Fact]
    public async Task Deleting_a_user_cascades_to_its_token_and_sync_state()
    {
        var userId = await SeedUserAsync(database, withToken: true, withSyncState: true);

        await using (var context = database.CreateContext())
        {
            // Assert the rows exist first, so the post-delete Empty assertions cannot
            // pass because nothing was ever written.
            Assert.NotNull(await context.DriveTokens.FindAsync(userId));
            Assert.NotNull(await context.DriveSyncStates.FindAsync(userId));

            context.Users.Remove(await context.Users.FindAsync(userId) ?? throw new InvalidOperationException());
            await context.SaveChangesAsync();
        }

        await using var verify = database.CreateContext();

        Assert.Null(await verify.DriveTokens.FindAsync(userId));
        Assert.Null(await verify.DriveSyncStates.FindAsync(userId));
    }

    /// <summary>
    /// The Google subject id is the account key, not the email: Google accounts can
    /// change email address, so keying on email would silently split one person's
    /// library in two.
    /// </summary>
    [Fact]
    public async Task Google_subject_id_is_unique()
    {
        const string subject = "duplicate-subject-000001";

        await using var context = database.CreateContext();

        context.Users.Add(NewUser(subject));
        await context.SaveChangesAsync();

        context.Users.Add(NewUser(subject));

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.IsType<SqliteException>(thrown.InnerException);
    }

    /// <summary>
    /// SQLite has no date type, so a timestamp is only sortable if it is stored in a
    /// lexicographically ordered form. Sorting in SQL rather than in memory is the
    /// whole point: an in-memory sort would pass with a wrong column type.
    /// </summary>
    [Fact]
    public async Task Timestamps_sort_correctly_in_sql()
    {
        var baseline = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var expected = new[]
        {
            baseline.AddYears(-1),
            baseline.AddDays(-40),
            baseline,
            baseline.AddMilliseconds(250),
            baseline.AddMonths(11),
        };

        var marker = $"sortcheck-{Guid.NewGuid():N}";

        await using (var context = database.CreateContext())
        {
            // Inserted out of order, and with mixed offsets, so a naive string column
            // that happened to be written in order would not accidentally pass.
            foreach (var stamp in expected.Reverse())
            {
                context.Users.Add(NewUser($"{marker}-{stamp.Ticks}", createdAt: stamp.ToOffset(TimeSpan.FromHours(5))));
            }

            await context.SaveChangesAsync();
        }

        await using var verify = database.CreateContext();

        var ordered = await verify.Users
            .Where(u => u.GoogleSubjectId.StartsWith(marker))
            .OrderBy(u => u.CreatedAtUtc)
            .Select(u => u.CreatedAtUtc)
            .ToListAsync();

        Assert.Equal(expected.Length, ordered.Count);
        Assert.Equal(expected.Select(e => e.UtcDateTime), ordered.Select(o => o.UtcDateTime));
    }

    [Fact]
    public async Task Sync_status_round_trips_as_a_readable_string()
    {
        var userId = await SeedUserAsync(database, withToken: false, withSyncState: true);

        await using (var context = database.CreateContext())
        {
            var state = await context.DriveSyncStates.FindAsync(userId)
                        ?? throw new InvalidOperationException();

            state.Status = SyncStatus.Running;
            await context.SaveChangesAsync();
        }

        await using var verify = database.CreateContext();

        // Read the raw column, but let EF bind the Guid parameter. Writing the
        // parameter by hand means guessing how the provider represents a Guid (TEXT or
        // a 16-byte BLOB); guessing wrong matches no rows and the assertion then fails
        // for a reason that has nothing to do with what is being tested.
        var stored = await verify.Database
            .SqlQuery<string>($"select Status as Value from DriveSyncStates where UserId = {userId}")
            .SingleAsync();

        // Stored as the enum name, not its ordinal: reordering the enum then cannot
        // silently remap existing rows, and the database stays readable by hand.
        Assert.Equal(nameof(SyncStatus.Running), stored);
    }

    private static async Task<Guid> SeedUserAsync(SqliteDatabase database, bool withToken, bool withSyncState)
    {
        await using var context = database.CreateContext();

        var user = NewUser($"subject-{Guid.NewGuid():N}");
        context.Users.Add(user);

        if (withToken)
        {
            context.DriveTokens.Add(new DriveToken
            {
                UserId = user.Id,
                EncryptedRefreshToken = "ciphertext",
                Scopes = "drive.readonly",
                GrantedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        if (withSyncState)
        {
            context.DriveSyncStates.Add(new DriveSyncState { UserId = user.Id });
        }

        await context.SaveChangesAsync();

        return user.Id;
    }

    private static User NewUser(string googleSubjectId, DateTimeOffset? createdAt = null) => new()
    {
        GoogleSubjectId = googleSubjectId,
        Email = "listener@example.com",
        DisplayName = "Listener",
        CreatedAtUtc = createdAt ?? DateTimeOffset.UtcNow,
        LastSeenAtUtc = createdAt ?? DateTimeOffset.UtcNow,
    };
}

public sealed class TokenProtectorTests : IDisposable
{
    private readonly string _keyRing;
    private readonly ITokenProtector _protector;

    public TokenProtectorTests()
    {
        _keyRing = Path.Combine(Path.GetTempPath(), "jukebox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_keyRing);

        _protector = CreateProtector(_keyRing);
    }

    [Fact]
    public void Round_trips_a_refresh_token()
    {
        const string token = "1//0gLiTeRaLlY-a-refresh-token_value.with-symbols";

        Assert.Equal(token, _protector.Unprotect(_protector.Protect(token)));
    }

    [Fact]
    public void Ciphertext_is_not_the_plaintext()
    {
        const string token = "1//refresh-token";

        Assert.DoesNotContain(token, _protector.Protect(token), StringComparison.Ordinal);
    }

    /// <summary>
    /// Data Protection is randomised, so the same input must not produce the same
    /// output. Identical ciphertext for identical tokens would let anyone with read
    /// access to the table see which users share a value.
    /// </summary>
    [Fact]
    public void Protecting_twice_yields_different_ciphertext()
    {
        const string token = "1//refresh-token";

        Assert.NotEqual(_protector.Protect(token), _protector.Protect(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64-at-all!!")]
    [InlineData("dGhpcyBpcyBiYXNlNjQgYnV0IG5vdCBwcm90ZWN0ZWQ=")]
    public void Unprotecting_rubbish_returns_null_rather_than_throwing(string ciphertext) =>
        Assert.Null(_protector.Unprotect(ciphertext));

    /// <summary>
    /// The realistic failure: the key ring was lost or replaced, which invalidates
    /// every stored token at once. That must degrade to "reconnect your Drive" rather
    /// than a 500 on every request.
    /// </summary>
    [Fact]
    public void A_different_key_ring_cannot_decrypt_and_does_not_throw()
    {
        var ciphertext = _protector.Protect("1//refresh-token");

        var otherRing = Path.Combine(Path.GetTempPath(), "jukebox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherRing);

        try
        {
            Assert.Null(CreateProtector(otherRing).Unprotect(ciphertext));
        }
        finally
        {
            Directory.Delete(otherRing, recursive: true);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_keyRing, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DataProtectionTokenProtector CreateProtector(string keyRing) =>
        new(
            DataProtectionProvider.Create(new DirectoryInfo(keyRing)),
            NullLogger<DataProtectionTokenProtector>.Instance);
}
