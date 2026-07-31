using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TheJukeBox.Domain;
using TheJukeBox.Features.Library;
using TheJukeBox.Infrastructure.Drive;

namespace TheJukeBox.Tests;

public class SyncBackoffTests
{
    [Fact]
    public void No_failures_means_no_delay() => Assert.Equal(TimeSpan.Zero, SyncBackoff.For(0));

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 20)]
    [InlineData(3, 45)]
    [InlineData(4, 80)]
    public void Grows_quadratically(int failures, double expectedMinutes) =>
        Assert.Equal(expectedMinutes, SyncBackoff.For(failures).TotalMinutes, precision: 3);

    [Fact]
    public void Is_capped()
    {
        // A revoked grant never recovers on its own, so retrying more often than a few times a
        // day achieves nothing -- but the delay must not grow without bound either, or a user
        // who fixes the problem waits effectively forever.
        Assert.Equal(SyncBackoff.Maximum, SyncBackoff.For(100));
        Assert.Equal(SyncBackoff.Maximum, SyncBackoff.For(int.MaxValue));
    }

    /// <summary>
    /// Squaring in <c>int</c> overflows around 46341 and wraps negative, which would turn a
    /// long-broken account into a hot retry loop rather than a backed-off one.
    /// </summary>
    [Theory]
    [InlineData(46341)]
    [InlineData(100000)]
    [InlineData(int.MaxValue)]
    public void Never_returns_a_negative_delay(int failures) =>
        Assert.True(SyncBackoff.For(failures) > TimeSpan.Zero);

    [Fact]
    public void A_healthy_user_is_always_due() =>
        Assert.True(SyncBackoff.IsDue(DateTimeOffset.UtcNow, consecutiveFailures: 0, DateTimeOffset.UtcNow));

    [Fact]
    public void A_failing_user_waits_then_becomes_due()
    {
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var lastAttempt = now - TimeSpan.FromMinutes(10);

        // Two failures means a 20 minute wait, and only 10 have passed.
        Assert.False(SyncBackoff.IsDue(lastAttempt, 2, now));
        Assert.True(SyncBackoff.IsDue(lastAttempt, 1, now));
    }

    [Fact]
    public void A_user_who_has_never_synced_is_due() =>
        Assert.True(SyncBackoff.IsDue(null, consecutiveFailures: 5, DateTimeOffset.UtcNow));
}

internal sealed class FakeDriveLibraryClient(params DriveAudioFile[] files) : IDriveLibraryClient
{
    public int EnumerationCount { get; private set; }

    public List<DriveAudioFile> Files { get; } = [.. files];

    public async IAsyncEnumerable<DriveAudioFile> EnumerateAudioAsync(
        Guid userId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnumerationCount++;

        foreach (var file in Files)
        {
            yield return file;
        }

        await Task.CompletedTask;
    }

    public Task<Dictionary<string, string>> ResolveFolderNamesAsync(
        Guid userId,
        IReadOnlyCollection<string> folderIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(folderIds.ToDictionary(id => id, _ => "Kid A", StringComparer.Ordinal));
}

internal sealed class FakeDriveChangesClient : IDriveChangesClient
{
    private readonly Queue<DriveChangePage> _pages = new();

    public string StartPageToken { get; set; } = "token-1";

    public bool ThrowExpired { get; set; }

    public int StartTokenRequests { get; private set; }

    public List<string> RequestedPageTokens { get; } = [];

    public void Enqueue(DriveChangePage page) => _pages.Enqueue(page);

    public Task<string> GetStartPageTokenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        StartTokenRequests++;
        return Task.FromResult(StartPageToken);
    }

    public Task<DriveChangePage> GetChangesAsync(
        Guid userId,
        string pageToken,
        CancellationToken cancellationToken = default)
    {
        RequestedPageTokens.Add(pageToken);

        if (ThrowExpired)
        {
            throw new DriveChangeTokenExpiredException("token expired");
        }

        return Task.FromResult(
            _pages.Count > 0
                ? _pages.Dequeue()
                : new DriveChangePage([], null, "token-final"));
    }
}

/// <summary>
/// The incremental path against a real database.
/// </summary>
/// <remarks>
/// A full listing of a large library costs hundreds of quota units every time, to discover
/// that nothing moved. These cover the change-feed path that replaces it, and in particular
/// the recovery route without which "automatic sync" works for months and then stops
/// permanently with nothing to indicate why.
/// </remarks>
public class IncrementalSyncTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    private static DriveAudioFile File(string id, string name, long size = 4096, string? md5 = null) =>
        new(id, name, "audio/flac", size, md5 ?? "md5-" + id, "rev-" + id, "folder1",
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1));

    private async Task<Guid> SeedUserAsync()
    {
        var userId = Guid.CreateVersion7();

        await using var context = database.CreateContext();

        context.Users.Add(new User
        {
            Id = userId,
            GoogleSubjectId = "subject-" + userId.ToString("N"),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();

        return userId;
    }

    private static LibrarySynchroniser Build(
        Infrastructure.Persistence.JukeBoxDbContext context,
        IDriveLibraryClient library,
        IDriveChangesClient changes) =>
        new(context, library, changes, NullLogger<LibrarySynchroniser>.Instance);

    /// <summary>
    /// The token must be taken before the listing, not after. Taken afterwards it would miss
    /// everything that changed while the listing ran; taken first, those changes are replayed
    /// next pass, which is free because reconciliation is idempotent.
    /// </summary>
    [Fact]
    public async Task A_full_sync_stores_a_change_token_for_next_time()
    {
        var userId = await SeedUserAsync();
        var library = new FakeDriveLibraryClient(File("f1", "01 Everything.flac"));
        var changes = new FakeDriveChangesClient { StartPageToken = "token-after-full" };

        await using (var context = database.CreateContext())
        {
            var result = await Build(context, library, changes).SynchroniseAsync(userId);
            Assert.Equal(1, result.Added);
        }

        Assert.Equal(1, changes.StartTokenRequests);

        await using var verify = database.CreateContext();
        var state = await verify.DriveSyncStates.SingleAsync(s => s.UserId == userId);

        Assert.Equal("token-after-full", state.ChangesStartPageToken);
        Assert.NotNull(state.LastFullSyncAtUtc);
    }

    [Fact]
    public async Task A_second_sync_uses_the_change_feed_instead_of_relisting()
    {
        var userId = await SeedUserAsync();
        var library = new FakeDriveLibraryClient(File("f1", "01 Everything.flac"));
        var changes = new FakeDriveChangesClient();

        await using (var context = database.CreateContext())
        {
            await Build(context, library, changes).SynchroniseAsync(userId);
        }

        Assert.Equal(1, library.EnumerationCount);

        await using (var context = database.CreateContext())
        {
            await Build(context, library, changes).SynchroniseAsync(userId);
        }

        // The whole point: no second full listing.
        Assert.Equal(1, library.EnumerationCount);
        Assert.NotEmpty(changes.RequestedPageTokens);
    }

    [Fact]
    public async Task A_change_adds_a_new_track()
    {
        var userId = await SeedUserAsync();
        var library = new FakeDriveLibraryClient(File("f1", "01 One.flac"));
        var changes = new FakeDriveChangesClient();

        await using (var context = database.CreateContext())
        {
            await Build(context, library, changes).SynchroniseAsync(userId);
        }

        changes.Enqueue(new DriveChangePage(
            [new DriveChange("f2", Removed: false, File("f2", "02 Two.flac"))],
            NextPageToken: null,
            NewStartPageToken: "token-2"));

        await using (var context = database.CreateContext())
        {
            var result = await Build(context, library, changes).SynchroniseAsync(userId);
            Assert.Equal(1, result.Added);
        }

        await using var verify = database.CreateContext();

        Assert.Equal(2, await verify.Tracks.CountAsync(t => t.UserId == userId && t.DeletedAtUtc == null));
        Assert.Equal("token-2", (await verify.DriveSyncStates.SingleAsync(s => s.UserId == userId)).ChangesStartPageToken);
    }

    [Fact]
    public async Task A_removal_soft_deletes_the_track()
    {
        var userId = await SeedUserAsync();
        var library = new FakeDriveLibraryClient(File("f1", "01 One.flac"), File("f2", "02 Two.flac"));
        var changes = new FakeDriveChangesClient();

        await using (var context = database.CreateContext())
        {
            await Build(context, library, changes).SynchroniseAsync(userId);
        }

        changes.Enqueue(new DriveChangePage(
            [new DriveChange("f2", Removed: true, File: null)],
            NextPageToken: null,
            NewStartPageToken: "token-2"));

        await using (var context = database.CreateContext())
        {
            var result = await Build(context, library, changes).SynchroniseAsync(userId);
            Assert.Equal(1, result.Removed);
        }

        await using var verify = database.CreateContext();

        // Soft, not hard: a playlist entry or history row pointing at a briefly trashed file
        // must survive, and un-trashing must restore rather than duplicate.
        var track = await verify.Tracks.SingleAsync(t => t.UserId == userId && t.DriveFileId == "f2");
        Assert.NotNull(track.DeletedAtUtc);
        Assert.Equal(1, await verify.Tracks.CountAsync(t => t.UserId == userId && t.DeletedAtUtc == null));
    }

    [Fact]
    public async Task A_change_for_an_unknown_file_is_ignored_rather_than_failing()
    {
        var userId = await SeedUserAsync();
        var library = new FakeDriveLibraryClient(File("f1", "01 One.flac"));
        var changes = new FakeDriveChangesClient();

        await using (var context = database.CreateContext())
        {
            await Build(context, library, changes).SynchroniseAsync(userId);
        }

        // Most changes in a real Drive concern files that are not music and were never
        // tracked, so this is the common case, not an edge one.
        changes.Enqueue(new DriveChangePage(
            [new DriveChange("never-seen", Removed: true, File: null)],
            NextPageToken: null,
            NewStartPageToken: "token-2"));

        await using var context2 = database.CreateContext();
        var result = await Build(context2, library, changes).SynchroniseAsync(userId);

        Assert.Equal(0, result.Removed);
    }

    /// <summary>
    /// The recovery path that must exist. Change tokens are long-lived but not eternal, and
    /// without this an instance syncs happily for months and then silently never again.
    /// </summary>
    [Fact]
    public async Task An_expired_token_falls_back_to_a_full_listing()
    {
        var userId = await SeedUserAsync();
        var library = new FakeDriveLibraryClient(File("f1", "01 One.flac"));
        var changes = new FakeDriveChangesClient();

        await using (var context = database.CreateContext())
        {
            await Build(context, library, changes).SynchroniseAsync(userId);
        }

        Assert.Equal(1, library.EnumerationCount);

        // Drive now rejects the stored token.
        changes.ThrowExpired = true;
        changes.StartPageToken = "token-recovered";

        await using (var context = database.CreateContext())
        {
            // Must not throw: recovery is automatic.
            await Build(context, library, changes).SynchroniseAsync(userId);
        }

        // It relisted, and it did not leave the library in a failed state.
        Assert.Equal(2, library.EnumerationCount);

        await using var verify = database.CreateContext();
        var state = await verify.DriveSyncStates.SingleAsync(s => s.UserId == userId);

        Assert.Equal(SyncStatus.Succeeded, state.Status);
        Assert.Equal(0, state.ConsecutiveFailures);

        // And a fresh token, so the next pass is incremental again rather than permanently
        // falling back to full listings.
        Assert.Equal("token-recovered", state.ChangesStartPageToken);
    }

    [Fact]
    public async Task Force_full_discards_the_change_token_and_relists()
    {
        var userId = await SeedUserAsync();
        var library = new FakeDriveLibraryClient(File("f1", "01 One.flac"));
        var changes = new FakeDriveChangesClient();

        await using (var context = database.CreateContext())
        {
            await Build(context, library, changes).SynchroniseAsync(userId);
        }

        await using (var context = database.CreateContext())
        {
            await Build(context, library, changes).SynchroniseAsync(userId, forceFull: true);
        }

        Assert.Equal(2, library.EnumerationCount);
        Assert.Empty(changes.RequestedPageTokens);
    }

    /// <summary>
    /// The token advances per page, in the same transaction as the page it describes, so a
    /// crash mid-feed resumes where it stopped rather than replaying from the start or, worse,
    /// skipping a page entirely.
    /// </summary>
    [Fact]
    public async Task The_token_advances_page_by_page()
    {
        var userId = await SeedUserAsync();
        var library = new FakeDriveLibraryClient(File("f1", "01 One.flac"));
        var changes = new FakeDriveChangesClient();

        await using (var context = database.CreateContext())
        {
            await Build(context, library, changes).SynchroniseAsync(userId);
        }

        changes.Enqueue(new DriveChangePage(
            [new DriveChange("f2", false, File("f2", "02 Two.flac"))],
            NextPageToken: "page-2",
            NewStartPageToken: null));

        changes.Enqueue(new DriveChangePage(
            [new DriveChange("f3", false, File("f3", "03 Three.flac"))],
            NextPageToken: null,
            NewStartPageToken: "token-final"));

        await using (var context = database.CreateContext())
        {
            var result = await Build(context, library, changes).SynchroniseAsync(userId);
            Assert.Equal(2, result.Added);
        }

        // Followed the continuation rather than stopping after one page.
        Assert.Contains("page-2", changes.RequestedPageTokens);

        await using var verify = database.CreateContext();

        Assert.Equal(3, await verify.Tracks.CountAsync(t => t.UserId == userId && t.DeletedAtUtc == null));
        Assert.Equal("token-final", (await verify.DriveSyncStates.SingleAsync(s => s.UserId == userId)).ChangesStartPageToken);
    }

    [Fact]
    public async Task An_empty_change_feed_leaves_everything_alone()
    {
        var userId = await SeedUserAsync();
        var library = new FakeDriveLibraryClient(File("f1", "01 One.flac"));
        var changes = new FakeDriveChangesClient();

        await using (var context = database.CreateContext())
        {
            await Build(context, library, changes).SynchroniseAsync(userId);
        }

        long versionBefore;

        await using (var context = database.CreateContext())
        {
            versionBefore = await context.Tracks.Where(t => t.UserId == userId).MaxAsync(t => t.RowVersion);
        }

        await using (var context = database.CreateContext())
        {
            var result = await Build(context, library, changes).SynchroniseAsync(userId);

            Assert.Equal(0, result.Added);
            Assert.Equal(0, result.Updated);
            Assert.Equal(0, result.Removed);
        }

        await using var verify = database.CreateContext();

        // The property a timer depends on: a refresh that finds nothing must not advance the
        // library version, or every client re-downloads everything every fifteen minutes.
        Assert.Equal(versionBefore, await verify.Tracks.Where(t => t.UserId == userId).MaxAsync(t => t.RowVersion));
    }
}
