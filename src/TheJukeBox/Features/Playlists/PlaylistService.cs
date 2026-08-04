using Microsoft.EntityFrameworkCore;
using TheJukeBox.Domain;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Playlists;

/// <summary>
/// Playlist reads and mutations, all scoped to one user.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes the owner's id and includes it in the predicate rather than loading a
/// playlist and then checking who owns it. A forgotten check is invisible in review; a missing
/// clause in a single-expression query is not, and the result is a 404 rather than a 403 --
/// confirming that an id exists but belongs to someone else is itself a disclosure.
/// </para>
/// <para>
/// Ordering lives in <see cref="PlaylistOrdering"/>, not here.
/// </para>
/// </remarks>
internal sealed class PlaylistService(JukeBoxDbContext context)
{
    /// <summary>
    /// The most items one request may add.
    /// </summary>
    /// <remarks>
    /// "Add my whole library to a playlist" is a legitimate thing to want and an illegitimate
    /// thing to do in one request: it would renumber tens of thousands of rows inside one
    /// transaction. The cap is above any realistic album or search result.
    /// </remarks>
    public const int MaxTracksPerRequest = 1000;

    /// <summary>Total items one playlist may hold.</summary>
    public const int MaxItemsPerPlaylist = 5000;

    public async Task<IReadOnlyList<PlaylistSummary>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await context.Playlists
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.UpdatedAtUtc)
            .Select(p => new PlaylistSummary(
                p.Id,
                p.Name,
                p.Description,
                p.CreatedAtUtc,
                p.UpdatedAtUtc,

                // Counted in SQL rather than by loading items. A playlists screen that loaded
                // every item of every playlist to show a number would get slower with use.
                p.Items.Count(i => i.Track!.DeletedAtUtc == null),

                // Nullable sum: SQLite returns null for the sum of no rows, and a playlist of
                // tracks whose durations are all unknown is indistinguishable from an empty
                // one otherwise.
                p.Items.Where(i => i.Track!.DeletedAtUtc == null).Sum(i => i.Track!.DurationSeconds ?? 0),
                p.Items.Any(i => i.Track!.DeletedAtUtc != null)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The playlist and its items in order, or null if it is not this user's.</summary>
    public async Task<PlaylistDetail?> GetAsync(
        Guid userId,
        Guid playlistId,
        CancellationToken cancellationToken)
    {
        var playlist = await context.Playlists
            .AsNoTracking()
            .Where(p => p.Id == playlistId && p.UserId == userId)
            .Select(p => new { p.Id, p.Name, p.Description, p.CreatedAtUtc, p.UpdatedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (playlist is null)
        {
            return null;
        }

        var items = await context.PlaylistItems
            .AsNoTracking()
            .Where(i => i.PlaylistId == playlistId)
            .OrderBy(i => i.Position)
            .Select(i => new PlaylistEntry(
                i.Id,
                i.Position,
                i.Track!.Id,
                i.Track.Title,
                i.Track.Artist,
                i.Track.Album,
                i.Track.DurationSeconds,
                i.Track.ArtSha256,

                // Unavailable items are returned, not filtered out. A playlist that silently
                // shrank because a file was trashed gives the user no way to understand what
                // happened; one that shows the entry greyed out does.
                i.Track.DeletedAtUtc == null))
            .ToListAsync(cancellationToken);

        return new PlaylistDetail(
            playlist.Id,
            playlist.Name,
            playlist.Description,
            playlist.CreatedAtUtc,
            playlist.UpdatedAtUtc,
            items);
    }

    public async Task<Playlist> CreateAsync(
        Guid userId,
        string name,
        string? description,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var playlist = new Playlist
        {
            UserId = userId,
            Name = name,
            Description = description,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        context.Playlists.Add(playlist);
        await context.SaveChangesAsync(cancellationToken);

        return playlist;
    }

    /// <summary>Renames or re-describes. Returns false when the playlist is not this user's.</summary>
    public async Task<bool> UpdateAsync(
        Guid userId,
        Guid playlistId,
        string? name,
        string? description,
        CancellationToken cancellationToken)
    {
        var playlist = await Owned(userId, playlistId).FirstOrDefaultAsync(cancellationToken);

        if (playlist is null)
        {
            return false;
        }

        if (name is not null)
        {
            playlist.Name = name;
        }

        // Distinguished from "not supplied" by the caller, which passes an empty string to
        // clear. Treating empty as absent would make a description impossible to remove.
        if (description is not null)
        {
            playlist.Description = description.Length == 0 ? null : description;
        }

        playlist.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid playlistId, CancellationToken cancellationToken)
    {
        var playlist = await Owned(userId, playlistId).FirstOrDefaultAsync(cancellationToken);

        if (playlist is null)
        {
            return false;
        }

        // Items go with it by cascade, configured on the relationship rather than done here so
        // it holds for any other path that removes a playlist.
        context.Playlists.Remove(playlist);
        await context.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Appends tracks to the end, in the order given.
    /// </summary>
    /// <remarks>
    /// Ids the user does not own, or that no longer exist, are skipped rather than failing the
    /// request: the realistic cause is a stale client list, and rejecting all forty additions
    /// because one file was trashed is not a useful response. The count of each is returned so
    /// the UI can say what happened.
    /// </remarks>
    public async Task<AddResult?> AddTracksAsync(
        Guid userId,
        Guid playlistId,
        IReadOnlyList<Guid> trackIds,
        CancellationToken cancellationToken)
    {
        var playlist = await Owned(userId, playlistId).FirstOrDefaultAsync(cancellationToken);

        if (playlist is null)
        {
            return null;
        }

        var requested = trackIds.Take(MaxTracksPerRequest).ToList();

        // One query for the whole batch, and it is the ownership check: a track id belonging
        // to another user simply does not come back.
        var valid = await context.Tracks
            .Where(t => t.UserId == userId && t.DeletedAtUtc == null && requested.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        var validSet = valid.ToHashSet();

        var existing = await context.PlaylistItems
            .Where(i => i.PlaylistId == playlistId)
            .CountAsync(cancellationToken);

        var room = Math.Max(MaxItemsPerPlaylist - existing, 0);

        // Deduplicated against the request, not against the playlist: adding a track that is
        // already there is allowed, but asking for the same id twice in one call is a client
        // bug rather than an intention.
        var toAdd = requested
            .Distinct()
            .Where(validSet.Contains)
            .Take(room)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var position = existing;

        foreach (var trackId in toAdd)
        {
            context.PlaylistItems.Add(new PlaylistItem
            {
                PlaylistId = playlistId,
                TrackId = trackId,

                // Appending past the current end, so these positions are unoccupied and no
                // renumbering is needed. Only reorder and remove disturb existing rows.
                Position = position++,
                AddedAtUtc = now,
            });
        }

        playlist.UpdatedAtUtc = now;

        await context.SaveChangesAsync(cancellationToken);

        return new AddResult(
            Added: toAdd.Count,
            Skipped: requested.Distinct().Count() - toAdd.Count,
            Total: existing + toAdd.Count);
    }

    /// <summary>Removes one item and closes the gap it left.</summary>
    public async Task<bool> RemoveItemAsync(
        Guid userId,
        Guid playlistId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var playlist = await Owned(userId, playlistId).FirstOrDefaultAsync(cancellationToken);

        if (playlist is null)
        {
            return false;
        }

        var items = await context.PlaylistItems
            .Where(i => i.PlaylistId == playlistId)
            .OrderBy(i => i.Position)
            .ToListAsync(cancellationToken);

        var target = items.FirstOrDefault(i => i.Id == itemId);

        if (target is null)
        {
            return false;
        }

        context.PlaylistItems.Remove(target);
        items.Remove(target);

        playlist.UpdatedAtUtc = DateTimeOffset.UtcNow;

        // Renumbered rather than left with a hole. The gap would be harmless to read, but the
        // "contiguous 0..n-1" invariant is what lets every other operation reason about
        // positions arithmetically instead of defensively.
        await PlaylistOrdering.RenumberAsync(context, items, cancellationToken);

        return true;
    }

    /// <summary>
    /// Sets the order from a full list of item ids.
    /// </summary>
    /// <remarks>
    /// The whole list, not "move item X to index N". A move is ambiguous the moment the client's
    /// view of the playlist is stale -- index 4 of what? -- whereas a complete list either
    /// matches the playlist or does not, and mismatches are detectable. Returns false on any
    /// mismatch, so a reorder against a changed playlist is refused rather than applied to the
    /// wrong rows.
    /// </remarks>
    public async Task<bool> ReorderAsync(
        Guid userId,
        Guid playlistId,
        IReadOnlyList<Guid> orderedItemIds,
        CancellationToken cancellationToken)
    {
        var playlist = await Owned(userId, playlistId).FirstOrDefaultAsync(cancellationToken);

        if (playlist is null)
        {
            return false;
        }

        var items = await context.PlaylistItems
            .Where(i => i.PlaylistId == playlistId)
            .ToListAsync(cancellationToken);

        if (orderedItemIds.Count != items.Count)
        {
            return false;
        }

        var byId = items.ToDictionary(i => i.Id);
        var ordered = new List<PlaylistItem>(items.Count);

        foreach (var id in orderedItemIds)
        {
            // Removing as we go rejects a list that repeats one id to pad out the count, which
            // a plain "is it present" check would accept.
            if (!byId.Remove(id, out var item))
            {
                return false;
            }

            ordered.Add(item);
        }

        playlist.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await PlaylistOrdering.RenumberAsync(context, ordered, cancellationToken);

        return true;
    }

    /// <summary>The playable track ids of a playlist, in order.</summary>
    /// <remarks>
    /// Unavailable tracks are excluded here, unlike in <see cref="GetAsync"/>: this feeds the
    /// player, and queueing a file that cannot be streamed only produces an error the user can
    /// do nothing about.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> TrackIdsAsync(
        Guid userId,
        Guid playlistId,
        CancellationToken cancellationToken)
    {
        return await context.PlaylistItems
            .AsNoTracking()
            .Where(i => i.PlaylistId == playlistId
                        && i.Playlist!.UserId == userId
                        && i.Track!.DeletedAtUtc == null)
            .OrderBy(i => i.Position)
            .Select(i => i.TrackId)
            .ToListAsync(cancellationToken);
    }

    private IQueryable<Playlist> Owned(Guid userId, Guid playlistId) =>
        context.Playlists.Where(p => p.Id == playlistId && p.UserId == userId);
}

public sealed record PlaylistSummary(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int TrackCount,
    int DurationSeconds,
    bool HasUnavailableTracks);

public sealed record PlaylistEntry(
    Guid ItemId,
    int Position,
    Guid TrackId,
    string Title,
    string? Artist,
    string? Album,
    int? DurationSeconds,
    string? ArtSha256,
    bool IsAvailable);

public sealed record PlaylistDetail(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<PlaylistEntry> Items);

public sealed record AddResult(int Added, int Skipped, int Total);
