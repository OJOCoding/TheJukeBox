using Microsoft.EntityFrameworkCore;
using TheJukeBox.Domain;
using TheJukeBox.Features.Playlists;

namespace TheJukeBox.Tests;

/// <summary>
/// Playlist mutations against the real migrated schema.
/// </summary>
/// <remarks>
/// The ordering invariant is enforced by a unique index, so these run against SQLite rather
/// than a fake: an in-memory double would accept the duplicate positions that the database
/// rejects, and the renumbering tests would then prove nothing.
/// </remarks>
public class PlaylistServiceTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    [Fact]
    public async Task A_new_playlist_starts_empty_and_is_listed()
    {
        var owner = await SeedUserAsync();

        await using (var context = database.CreateContext())
        {
            await new PlaylistService(context).CreateAsync(owner, "Late night", "for driving", default);
        }

        await using var read = database.CreateContext();

        var listed = await new PlaylistService(read).ListAsync(owner, default);

        var playlist = Assert.Single(listed);

        Assert.Equal("Late night", playlist.Name);
        Assert.Equal("for driving", playlist.Description);
        Assert.Equal(0, playlist.TrackCount);
        Assert.Equal(0, playlist.DurationSeconds);
        Assert.False(playlist.HasUnavailableTracks);
    }

    [Fact]
    public async Task Adding_tracks_appends_them_in_the_order_given()
    {
        var owner = await SeedUserAsync();
        var tracks = await SeedTracksAsync(owner, 4);
        var playlist = await CreateAsync(owner, "Ordered");

        // Reversed, so an accidental sort by title or id would be visible.
        var requested = tracks.AsEnumerable().Reverse().ToList();

        await using (var context = database.CreateContext())
        {
            var result = await new PlaylistService(context).AddTracksAsync(owner, playlist, requested, default);

            Assert.NotNull(result);
            Assert.Equal(4, result!.Added);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(4, result.Total);
        }

        await using var read = database.CreateContext();

        var detail = await new PlaylistService(read).GetAsync(owner, playlist, default);

        Assert.NotNull(detail);
        Assert.Equal(requested, detail!.Items.Select(i => i.TrackId));
        Assert.Equal([0, 1, 2, 3], detail.Items.Select(i => i.Position));
    }

    /// <summary>
    /// The cross-user case. A track id from another library must not be addable, or a playlist
    /// becomes a way to enumerate and stream someone else's files.
    /// </summary>
    [Fact]
    public async Task Another_users_tracks_are_skipped_rather_than_added()
    {
        var owner = await SeedUserAsync();
        var stranger = await SeedUserAsync();

        var mine = await SeedTracksAsync(owner, 1);
        var theirs = await SeedTracksAsync(stranger, 2);

        var playlist = await CreateAsync(owner, "Mixed");

        await using (var context = database.CreateContext())
        {
            var result = await new PlaylistService(context)
                .AddTracksAsync(owner, playlist, [.. mine, .. theirs], default);

            Assert.Equal(1, result!.Added);
            Assert.Equal(2, result.Skipped);
        }

        await using var read = database.CreateContext();

        var detail = await new PlaylistService(read).GetAsync(owner, playlist, default);

        Assert.Equal(mine, detail!.Items.Select(i => i.TrackId));
    }

    [Fact]
    public async Task A_soft_deleted_track_is_not_addable()
    {
        var owner = await SeedUserAsync();
        var tracks = await SeedTracksAsync(owner, 2);
        var playlist = await CreateAsync(owner, "Trashed");

        await using (var context = database.CreateContext())
        {
            var track = await context.Tracks.SingleAsync(t => t.Id == tracks[0]);
            track.DeletedAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await using (var context = database.CreateContext())
        {
            var result = await new PlaylistService(context).AddTracksAsync(owner, playlist, tracks, default);

            Assert.Equal(1, result!.Added);
            Assert.Equal(1, result.Skipped);
        }
    }

    /// <summary>
    /// A file trashed *after* it was added stays in the playlist, marked unavailable. Silently
    /// dropping it would leave the user unable to tell a shrinking playlist from a bug.
    /// </summary>
    [Fact]
    public async Task An_item_whose_track_was_trashed_is_kept_but_marked_unavailable()
    {
        var owner = await SeedUserAsync();
        var tracks = await SeedTracksAsync(owner, 2);
        var playlist = await CreateAsync(owner, "Kept");

        await using (var context = database.CreateContext())
        {
            await new PlaylistService(context).AddTracksAsync(owner, playlist, tracks, default);
        }

        await using (var context = database.CreateContext())
        {
            var track = await context.Tracks.SingleAsync(t => t.Id == tracks[1]);
            track.DeletedAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await using var read = database.CreateContext();
        var service = new PlaylistService(read);

        var detail = await service.GetAsync(owner, playlist, default);

        Assert.Equal(2, detail!.Items.Count);
        Assert.True(detail.Items[0].IsAvailable);
        Assert.False(detail.Items[1].IsAvailable);

        // But the player is only handed what it can actually stream.
        Assert.Equal([tracks[0]], await service.TrackIdsAsync(owner, playlist, default));

        var summary = Assert.Single(await service.ListAsync(owner, default));

        Assert.Equal(1, summary.TrackCount);
        Assert.True(summary.HasUnavailableTracks);
    }

    [Fact]
    public async Task Removing_an_item_closes_the_gap()
    {
        var owner = await SeedUserAsync();
        var tracks = await SeedTracksAsync(owner, 4);
        var playlist = await CreateAsync(owner, "Compacted");

        List<PlaylistEntry> before;

        await using (var context = database.CreateContext())
        {
            var service = new PlaylistService(context);
            await service.AddTracksAsync(owner, playlist, tracks, default);
            before = [.. (await service.GetAsync(owner, playlist, default))!.Items];
        }

        await using (var context = database.CreateContext())
        {
            // The middle, so the renumbering has to shift the rows after it.
            Assert.True(await new PlaylistService(context)
                .RemoveItemAsync(owner, playlist, before[1].ItemId, default));
        }

        await using var read = database.CreateContext();

        var after = (await new PlaylistService(read).GetAsync(owner, playlist, default))!.Items;

        Assert.Equal([0, 1, 2], after.Select(i => i.Position));
        Assert.Equal(
            [before[0].ItemId, before[2].ItemId, before[3].ItemId],
            after.Select(i => i.ItemId));
    }

    [Fact]
    public async Task Reordering_applies_the_requested_order()
    {
        var owner = await SeedUserAsync();
        var tracks = await SeedTracksAsync(owner, 5);
        var playlist = await CreateAsync(owner, "Shuffled by hand");

        List<Guid> itemIds;

        await using (var context = database.CreateContext())
        {
            var service = new PlaylistService(context);
            await service.AddTracksAsync(owner, playlist, tracks, default);
            itemIds = [.. (await service.GetAsync(owner, playlist, default))!.Items.Select(i => i.ItemId)];
        }

        // A rotation: every item moves, and no accidental sort reproduces it.
        var requested = new List<Guid> { itemIds[2], itemIds[4], itemIds[0], itemIds[3], itemIds[1] };

        await using (var context = database.CreateContext())
        {
            Assert.True(await new PlaylistService(context).ReorderAsync(owner, playlist, requested, default));
        }

        await using var read = database.CreateContext();

        var after = (await new PlaylistService(read).GetAsync(owner, playlist, default))!.Items;

        Assert.Equal(requested, after.Select(i => i.ItemId));
        Assert.Equal([0, 1, 2, 3, 4], after.Select(i => i.Position));
    }

    /// <summary>
    /// A reorder built from a stale view of the playlist must be refused, not applied to
    /// whatever rows happen to match. Applying it would scramble the order rather than fail.
    /// </summary>
    [Theory]
    [InlineData("short")]
    [InlineData("padded")]
    [InlineData("foreign")]
    public async Task A_reorder_that_does_not_match_the_playlist_is_refused(string flavour)
    {
        var owner = await SeedUserAsync();
        var tracks = await SeedTracksAsync(owner, 3);
        var playlist = await CreateAsync(owner, "Stale");

        List<Guid> itemIds;

        await using (var context = database.CreateContext())
        {
            var service = new PlaylistService(context);
            await service.AddTracksAsync(owner, playlist, tracks, default);
            itemIds = [.. (await service.GetAsync(owner, playlist, default))!.Items.Select(i => i.ItemId)];
        }

        List<Guid> requested = flavour switch
        {
            // One short: an item was added by another tab since the client loaded it.
            "short" => [itemIds[0], itemIds[1]],

            // Right length, but one id repeated to pad it. A plain "are they all present"
            // check would accept this and silently drop an item.
            "padded" => [itemIds[0], itemIds[1], itemIds[1]],

            // Right length, one id from nowhere.
            _ => [itemIds[0], itemIds[1], Guid.CreateVersion7()],
        };

        await using (var context = database.CreateContext())
        {
            Assert.False(await new PlaylistService(context).ReorderAsync(owner, playlist, requested, default));
        }

        await using var read = database.CreateContext();

        var after = (await new PlaylistService(read).GetAsync(owner, playlist, default))!.Items;

        // Untouched: a refused reorder must not half-apply.
        Assert.Equal(itemIds, after.Select(i => i.ItemId));
    }

    /// <summary>
    /// Cross-user access answers "not found" rather than "forbidden": confirming an id exists
    /// but belongs to someone else is itself a disclosure.
    /// </summary>
    [Fact]
    public async Task Another_users_playlist_is_invisible_and_immutable()
    {
        var owner = await SeedUserAsync();
        var stranger = await SeedUserAsync();

        var tracks = await SeedTracksAsync(owner, 2);
        var playlist = await CreateAsync(owner, "Private");

        await using (var context = database.CreateContext())
        {
            await new PlaylistService(context).AddTracksAsync(owner, playlist, tracks, default);
        }

        await using var context2 = database.CreateContext();
        var asStranger = new PlaylistService(context2);

        Assert.Null(await asStranger.GetAsync(stranger, playlist, default));
        Assert.Empty(await asStranger.ListAsync(stranger, default));
        Assert.Empty(await asStranger.TrackIdsAsync(stranger, playlist, default));
        Assert.False(await asStranger.UpdateAsync(stranger, playlist, "Hijacked", null, default));
        Assert.False(await asStranger.ReorderAsync(stranger, playlist, [], default));
        Assert.Null(await asStranger.AddTracksAsync(stranger, playlist, tracks, default));
        Assert.False(await asStranger.DeleteAsync(stranger, playlist, default));

        await using var verify = database.CreateContext();

        var detail = await new PlaylistService(verify).GetAsync(owner, playlist, default);

        Assert.Equal("Private", detail!.Name);
        Assert.Equal(2, detail.Items.Count);
    }

    /// <summary>
    /// Adding forty tracks is a change to the playlist. If it did not touch UpdatedAtUtc, the
    /// "recently changed" ordering on the playlists screen would be sorted by rename date.
    /// </summary>
    [Fact]
    public async Task Item_changes_touch_the_playlists_timestamp()
    {
        var owner = await SeedUserAsync();
        var tracks = await SeedTracksAsync(owner, 1);
        var playlist = await CreateAsync(owner, "Touched");

        DateTimeOffset created;

        await using (var read = database.CreateContext())
        {
            created = (await new PlaylistService(read).GetAsync(owner, playlist, default))!.UpdatedAtUtc;
        }

        await using (var context = database.CreateContext())
        {
            await new PlaylistService(context).AddTracksAsync(owner, playlist, tracks, default);
        }

        await using var verify = database.CreateContext();

        var updated = (await new PlaylistService(verify).GetAsync(owner, playlist, default))!.UpdatedAtUtc;

        Assert.True(updated >= created, $"{updated:O} should not precede {created:O}");
        Assert.NotEqual(created, updated);
    }

    [Fact]
    public async Task A_description_is_cleared_by_an_empty_string_and_kept_by_null()
    {
        var owner = await SeedUserAsync();
        var playlist = await CreateAsync(owner, "Described", "the original text");

        await using (var context = database.CreateContext())
        {
            // Null description: a rename should not silently wipe it.
            await new PlaylistService(context).UpdateAsync(owner, playlist, "Renamed", null, default);
        }

        await using (var read = database.CreateContext())
        {
            var detail = await new PlaylistService(read).GetAsync(owner, playlist, default);
            Assert.Equal("Renamed", detail!.Name);
            Assert.Equal("the original text", detail.Description);
        }

        await using (var context = database.CreateContext())
        {
            await new PlaylistService(context).UpdateAsync(owner, playlist, null, string.Empty, default);
        }

        await using var verify = database.CreateContext();

        var cleared = await new PlaylistService(verify).GetAsync(owner, playlist, default);

        Assert.Equal("Renamed", cleared!.Name);
        Assert.Null(cleared.Description);
    }

    [Fact]
    public async Task Duration_is_summed_over_available_tracks_only()
    {
        var owner = await SeedUserAsync();
        var tracks = await SeedTracksAsync(owner, 3, durationSeconds: 100);
        var playlist = await CreateAsync(owner, "Timed");

        await using (var context = database.CreateContext())
        {
            await new PlaylistService(context).AddTracksAsync(owner, playlist, tracks, default);
        }

        await using (var context = database.CreateContext())
        {
            var track = await context.Tracks.SingleAsync(t => t.Id == tracks[2]);
            track.DeletedAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await using var read = database.CreateContext();

        var summary = Assert.Single(await new PlaylistService(read).ListAsync(owner, default));

        Assert.Equal(200, summary.DurationSeconds);
    }

    /// <summary>A track with no known duration must not make the total null or throw.</summary>
    [Fact]
    public async Task An_unknown_duration_counts_as_zero()
    {
        var owner = await SeedUserAsync();
        var tracks = await SeedTracksAsync(owner, 2, durationSeconds: null);
        var playlist = await CreateAsync(owner, "Unknown");

        await using (var context = database.CreateContext())
        {
            await new PlaylistService(context).AddTracksAsync(owner, playlist, tracks, default);
        }

        await using var read = database.CreateContext();

        var summary = Assert.Single(await new PlaylistService(read).ListAsync(owner, default));

        Assert.Equal(0, summary.DurationSeconds);
        Assert.Equal(2, summary.TrackCount);
    }

    private async Task<Guid> CreateAsync(Guid owner, string name, string? description = null)
    {
        await using var context = database.CreateContext();

        var playlist = await new PlaylistService(context).CreateAsync(owner, name, description, default);

        return playlist.Id;
    }

    private async Task<Guid> SeedUserAsync()
    {
        await using var context = database.CreateContext();

        var user = new User
        {
            GoogleSubjectId = $"subject-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow,
            Status = UserStatus.Active,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<List<Guid>> SeedTracksAsync(Guid userId, int count, int? durationSeconds = 180)
    {
        await using var context = database.CreateContext();

        var ids = new List<Guid>();

        for (var index = 0; index < count; index++)
        {
            var track = new Track
            {
                UserId = userId,
                DriveFileId = $"drive-{Guid.NewGuid():N}",
                Name = $"track-{index}.mp3",
                Title = $"track-{index}",
                SearchText = $"track-{index}",
                DurationSeconds = durationSeconds,
                AddedAtUtc = DateTimeOffset.UtcNow,
            };

            context.Tracks.Add(track);
            ids.Add(track.Id);
        }

        await context.SaveChangesAsync();

        return ids;
    }
}
