using Microsoft.EntityFrameworkCore;
using TheJukeBox.Domain;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Tests;

/// <summary>
/// Why reordering a playlist is written in two passes.
/// </summary>
/// <remarks>
/// The unique index on (PlaylistId, Position) is what stops two items sharing a position --
/// a bug that would surface as tracks quietly swapping places rather than as an error. EF
/// knows about that index and topologically sorts its UPDATEs to avoid transient collisions,
/// which covers a shift (0,1,2 becoming 1,2,3) but cannot cover a cycle: a swap has no valid
/// order, so EF refuses to schedule it at all. These tests pin both halves: the naive write
/// really does fail, and the two-pass writer really does succeed.
/// </remarks>
public class PlaylistOrderingTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    /// <summary>
    /// The failure the two-pass writer exists to avoid.
    /// </summary>
    /// <remarks>
    /// Note what is thrown: <see cref="InvalidOperationException"/> from EF's own dependency
    /// sort, not a constraint violation from SQLite. The write never reaches the database. If
    /// a future EF learns to break these cycles by parking rows itself, this test fails and
    /// the extra pass can be deleted with evidence rather than guessed at.
    /// </remarks>
    [Fact]
    public async Task Renumbering_in_one_pass_is_refused_as_a_dependency_cycle()
    {
        var (playlist, items) = await SeedAsync(trackCount: 2);

        await using var context = database.CreateContext();

        var rows = await context.PlaylistItems
            .Where(i => i.PlaylistId == playlist)
            .OrderBy(i => i.Position)
            .ToListAsync();

        // Swap them, the obvious way.
        rows[0].Position = 1;
        rows[1].Position = 0;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Contains("circular dependency", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task Renumbering_in_two_passes_succeeds()
    {
        var (playlist, _) = await SeedAsync(trackCount: 4);

        await using (var context = database.CreateContext())
        {
            var rows = await context.PlaylistItems
                .Where(i => i.PlaylistId == playlist)
                .OrderBy(i => i.Position)
                .ToListAsync();

            // Reverse: the worst case, where every item's new position is occupied.
            await PlaylistOrdering.RenumberAsync(context, rows.AsEnumerable().Reverse().ToList(), default);
        }

        await using var verify = database.CreateContext();

        var positions = await verify.PlaylistItems
            .Where(i => i.PlaylistId == playlist)
            .OrderBy(i => i.Position)
            .Select(i => i.Position)
            .ToListAsync();

        Assert.Equal([0, 1, 2, 3], positions);
    }

    /// <summary>
    /// The point of the exercise: the order the caller asked for is the order that comes back.
    /// A test that only checked the positions were 0..n-1 would pass on any permutation.
    /// </summary>
    [Fact]
    public async Task Renumbering_preserves_the_requested_order()
    {
        var (playlist, items) = await SeedAsync(trackCount: 5);

        // A rotation, which no accidental sort produces.
        var requested = new List<Guid> { items[3], items[0], items[4], items[1], items[2] };

        await using (var context = database.CreateContext())
        {
            var rows = await context.PlaylistItems.Where(i => i.PlaylistId == playlist).ToListAsync();
            var byId = rows.ToDictionary(i => i.Id);

            await PlaylistOrdering.RenumberAsync(context, [.. requested.Select(id => byId[id])], default);
        }

        await using var verify = database.CreateContext();

        var actual = await verify.PlaylistItems
            .Where(i => i.PlaylistId == playlist)
            .OrderBy(i => i.Position)
            .Select(i => i.Id)
            .ToListAsync();

        Assert.Equal(requested, actual);
    }

    [Fact]
    public async Task Duplicate_positions_are_rejected_outright()
    {
        var (playlist, _) = await SeedAsync(trackCount: 1);

        await using var context = database.CreateContext();

        var track = await context.Tracks.FirstAsync(t => t.Title.StartsWith("seed-"));

        context.PlaylistItems.Add(new PlaylistItem
        {
            PlaylistId = playlist,
            TrackId = track.Id,

            // Position 0 is already taken by the seeded item.
            Position = 0,
            AddedAtUtc = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    /// <summary>
    /// The same track twice is legitimate -- a set that closes with the track it opened with.
    /// A composite (playlist, track) key would have refused it.
    /// </summary>
    [Fact]
    public async Task The_same_track_may_appear_twice_in_one_playlist()
    {
        var (playlist, _) = await SeedAsync(trackCount: 1);

        await using (var context = database.CreateContext())
        {
            var existing = await context.PlaylistItems.FirstAsync(i => i.PlaylistId == playlist);

            context.PlaylistItems.Add(new PlaylistItem
            {
                PlaylistId = playlist,
                TrackId = existing.TrackId,
                Position = 1,
                AddedAtUtc = DateTimeOffset.UtcNow,
            });

            await context.SaveChangesAsync();
        }

        await using var verify = database.CreateContext();

        var trackIds = await verify.PlaylistItems
            .Where(i => i.PlaylistId == playlist)
            .Select(i => i.TrackId)
            .ToListAsync();

        Assert.Equal(2, trackIds.Count);
        Assert.Single(trackIds.Distinct());
    }

    [Fact]
    public async Task Deleting_a_playlist_removes_its_items_but_not_the_tracks()
    {
        var (playlist, _) = await SeedAsync(trackCount: 3);

        await using (var context = database.CreateContext())
        {
            context.Playlists.Remove(await context.Playlists.SingleAsync(p => p.Id == playlist));
            await context.SaveChangesAsync();
        }

        await using var verify = database.CreateContext();

        Assert.Empty(await verify.PlaylistItems.Where(i => i.PlaylistId == playlist).ToListAsync());
        Assert.NotEmpty(await verify.Tracks.Where(t => t.Title.StartsWith("seed-")).ToListAsync());
    }

    /// <summary>Seeds a playlist of <paramref name="trackCount"/> items at positions 0..n-1.</summary>
    private async Task<(Guid PlaylistId, List<Guid> ItemIds)> SeedAsync(int trackCount)
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

        var playlist = new Playlist
        {
            UserId = user.Id,
            Name = "Seeded",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        context.Playlists.Add(playlist);

        var itemIds = new List<Guid>();

        for (var index = 0; index < trackCount; index++)
        {
            var track = new Track
            {
                UserId = user.Id,
                DriveFileId = $"drive-{Guid.NewGuid():N}",
                Name = $"seed-{index}.mp3",
                Title = $"seed-{index}",
                SearchText = $"seed-{index}",
                AddedAtUtc = DateTimeOffset.UtcNow,
            };

            context.Tracks.Add(track);

            var item = new PlaylistItem
            {
                PlaylistId = playlist.Id,
                TrackId = track.Id,
                Position = index,
                AddedAtUtc = DateTimeOffset.UtcNow,
            };

            context.PlaylistItems.Add(item);
            itemIds.Add(item.Id);
        }

        await context.SaveChangesAsync();

        return (playlist.Id, itemIds);
    }
}
