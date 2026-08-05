using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TheJukeBox.Domain;
using TheJukeBox.Features.Library;
using TheJukeBox.Infrastructure.Drive;
using TheJukeBox.Infrastructure.Metadata;

namespace TheJukeBox.Tests;

/// <summary>
/// The handoff between the two passes of a sync.
/// </summary>
/// <remarks>
/// <para>
/// A sync has two passes. The first reconciles rows against Drive and guesses metadata from file
/// names; the second reads embedded tags, which is where durations and cover art come from. The
/// second pass finds its work by asking which tracks it has not examined yet, and
/// <c>MetadataExtractedAtUtc</c> is the column that answers.
/// </para>
/// <para>
/// So the two passes share a contract, and nothing in either of their own test suites could catch
/// them disagreeing about it: the enricher's tests seed tracks directly, and the synchroniser's
/// tests never ask what the enricher would do next. This file is that missing seam. It exists
/// because the two halves really did disagree -- the reconciler stamped the column when it wrote a
/// filename guess, so the tag pass concluded every file had already been examined and read nothing,
/// ever. Every library had blank durations and no album art, and no test failed.
/// </para>
/// </remarks>
public class SyncEnricherHandoffTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    /// <summary>
    /// A fixed instant, not <c>UtcNow</c>.
    /// </summary>
    /// <remarks>
    /// Two calls describing "the same file" have to produce byte-identical values, or the
    /// reconciler correctly sees a changed modified-time and rewrites the row -- which looks
    /// exactly like the churn bug one of these tests is asserting against.
    /// </remarks>
    private static readonly DateTimeOffset Modified = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static DriveAudioFile File(
        string id,
        string name,
        string? md5 = null,
        DateTimeOffset? modified = null) =>
        new(id, name, "audio/flac", 4096, md5 ?? "md5-" + id, "rev-" + id, "folder1",
            Modified, modified ?? Modified);

    /// <summary>
    /// The regression. After a first sync every track must be waiting for the tag pass, because
    /// nothing has read a single byte of any of them yet.
    /// </summary>
    [Fact]
    public async Task A_freshly_synced_track_is_waiting_for_the_tag_pass()
    {
        var userId = await SeedUserAsync();

        await SyncAsync(userId, File("f1", "01 Everything In Its Right Place.flac"));

        await using var verify = database.CreateContext();

        var track = await verify.Tracks.SingleAsync(t => t.UserId == userId);

        // The claim that matters: the reconciler has not pretended to have read tags.
        Assert.Null(track.MetadataExtractedAtUtc);
        Assert.Equal(MetadataSource.Filename, track.MetadataSource);

        // And therefore the enricher has work to do. Asserted through the enricher's own
        // candidate selection rather than by restating its query here, so the two cannot drift.
        Assert.Equal(1, await CandidateCountAsync(userId));
    }

    /// <summary>
    /// The reason durations and album art were missing: no candidates meant no tag reads.
    /// </summary>
    [Fact]
    public async Task A_sync_of_many_files_leaves_all_of_them_to_be_read()
    {
        var userId = await SeedUserAsync();

        var files = Enumerable.Range(0, 12)
            .Select(index => File($"f{index}", $"{index:00} Track.flac"))
            .ToArray();

        await SyncAsync(userId, files);

        Assert.Equal(12, await CandidateCountAsync(userId));
    }

    /// <summary>
    /// The other half of the contract, and the reason the naive fix is wrong: once the tag pass
    /// has examined a file it must not be examined again. A genuinely untagged file keeps
    /// <see cref="MetadataSource.Filename"/> forever, so a "source is not Tags" test would
    /// re-download the front of it on every sync, for ever.
    /// </summary>
    [Fact]
    public async Task A_track_the_tag_pass_has_examined_is_not_offered_again()
    {
        var userId = await SeedUserAsync();

        await SyncAsync(userId, File("f1", "01 Untagged.flac"));

        // Stand in for a completed tag pass that found no tags: examined at this fingerprint,
        // still filename-derived.
        await using (var context = database.CreateContext())
        {
            var track = await context.Tracks.SingleAsync(t => t.UserId == userId);

            track.MetadataExtractedAtUtc = DateTimeOffset.UtcNow;
            track.MetadataFingerprint = track.Md5Checksum;

            await context.SaveChangesAsync();
        }

        Assert.Equal(0, await CandidateCountAsync(userId));

        // A second sync must not undo that, or the pass never terminates.
        await SyncAsync(userId, File("f1", "01 Untagged.flac"));

        Assert.Equal(0, await CandidateCountAsync(userId));
    }

    /// <summary>
    /// A pure rename must not trigger a re-read: re-reading is a network cost per file, and
    /// renaming a file does not change its tags.
    /// </summary>
    [Fact]
    public async Task A_rename_alone_does_not_send_a_track_back_to_the_tag_pass()
    {
        var userId = await SeedUserAsync();

        await SyncAsync(userId, File("f1", "01 Original Name.flac"));

        await MarkExaminedAsync(userId);

        Assert.Equal(0, await CandidateCountAsync(userId));

        // Same md5, different name.
        await SyncAsync(userId, File("f1", "01 Renamed.flac"));

        Assert.Equal(0, await CandidateCountAsync(userId));

        await using var verify = database.CreateContext();

        var track = await verify.Tracks.SingleAsync(t => t.UserId == userId);

        // The filename guess is refreshed, though, because that is what it derives from.
        Assert.Equal("01 Renamed.flac", track.Name);
    }

    /// <summary>
    /// An edit must trigger a re-read, including when the existing metadata came from tags --
    /// otherwise re-tagging a file in Picard would never show up.
    /// </summary>
    [Fact]
    public async Task Edited_content_sends_a_track_back_to_the_tag_pass()
    {
        var userId = await SeedUserAsync();

        await SyncAsync(userId, File("f1", "01 Song.flac", md5: "md5-before"));

        // Stand in for a successful tag read.
        await using (var context = database.CreateContext())
        {
            var track = await context.Tracks.SingleAsync(t => t.UserId == userId);

            track.MetadataSource = MetadataSource.Tags;
            track.MetadataExtractedAtUtc = DateTimeOffset.UtcNow;
            track.MetadataFingerprint = "md5-before";
            track.Title = "Song";
            track.DurationSeconds = 240;

            await context.SaveChangesAsync();
        }

        Assert.Equal(0, await CandidateCountAsync(userId));

        // The file was re-tagged: same id, new content.
        await SyncAsync(userId, File("f1", "01 Song.flac", md5: "md5-after"));

        Assert.Equal(1, await CandidateCountAsync(userId));

        await using var verify = database.CreateContext();

        var edited = await verify.Tracks.SingleAsync(t => t.UserId == userId);

        // Tag-derived values survive until the new read replaces them: showing the old title is
        // better than blanking the row while the pass catches up.
        Assert.Equal("Song", edited.Title);
        Assert.Equal(MetadataSource.Tags, edited.MetadataSource);
    }

    /// <summary>
    /// A sync that changes nothing must not mark rows as changed. RowVersion drives delta sync,
    /// so churning it every 15 minutes for every untagged file would make the change feed useless.
    /// </summary>
    [Fact]
    public async Task An_unchanged_file_is_not_rewritten_on_a_later_sync()
    {
        var userId = await SeedUserAsync();

        await SyncAsync(userId, File("f1", "01 Song.flac"));
        await MarkExaminedAsync(userId);

        long versionAfterFirst;

        await using (var read = database.CreateContext())
        {
            versionAfterFirst = (await read.Tracks.SingleAsync(t => t.UserId == userId)).RowVersion;
        }

        await SyncAsync(userId, File("f1", "01 Song.flac"));

        await using var verify = database.CreateContext();

        var track = await verify.Tracks.SingleAsync(t => t.UserId == userId);

        Assert.Equal(versionAfterFirst, track.RowVersion);
    }

    /// <summary>Runs a full sync against a fake Drive listing.</summary>
    private async Task SyncAsync(Guid userId, params DriveAudioFile[] files)
    {
        await using var context = database.CreateContext();

        var synchroniser = new LibrarySynchroniser(
            context,
            new FakeDriveLibraryClient(files),
            new FakeDriveChangesClient { StartPageToken = "token" },
            NullLogger<LibrarySynchroniser>.Instance);

        await synchroniser.SynchroniseAsync(userId, forceFull: true);
    }

    /// <summary>
    /// How many tracks the tag pass would read, asked through the enricher itself.
    /// </summary>
    /// <remarks>
    /// Via <c>CountPendingAsync</c>, which shares its predicate with the real candidate query.
    /// Restating that predicate here would let the two halves drift apart again, which is exactly
    /// the failure this file exists to catch -- so the seam is tested, not a copy of it.
    /// </remarks>
    private async Task<int> CandidateCountAsync(Guid userId)
    {
        await using var context = database.CreateContext();

        var enricher = new MetadataEnricher(
            context,
            new StubDriveClientFactory(),
            new SingleClientFactory(new FakeDriveMediaHandler([])),
            new AtlTagReader(NullLogger<AtlTagReader>.Instance),
            NullLogger<MetadataEnricher>.Instance);

        return await enricher.CountPendingAsync(userId);
    }

    /// <summary>Marks every track as examined at its current fingerprint.</summary>
    private async Task MarkExaminedAsync(Guid userId)
    {
        await using var context = database.CreateContext();

        foreach (var track in await context.Tracks.Where(t => t.UserId == userId).ToListAsync())
        {
            track.MetadataExtractedAtUtc = DateTimeOffset.UtcNow;
            track.MetadataFingerprint = track.Md5Checksum;
        }

        await context.SaveChangesAsync();
    }

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
            Status = UserStatus.Active,
        });

        await context.SaveChangesAsync();

        return userId;
    }
}
