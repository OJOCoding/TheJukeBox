namespace TheJukeBox.Domain;

/// <summary>
/// An ordered, named list of tracks a user assembled by hand.
/// </summary>
/// <remarks>
/// Private to its owner. There is no sharing model and no public playlists: this instance
/// holds other people's private libraries, and a share link would be a way for one user's
/// mistake to expose another's file names.
/// </remarks>
public sealed class Playlist
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public User? User { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// Touched whenever the playlist or its contents change.
    /// </summary>
    /// <remarks>
    /// Updated by item mutations too, not only by a rename. "Recently changed" ordering is
    /// useless if adding forty tracks does not count as a change.
    /// </remarks>
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ICollection<PlaylistItem> Items { get; } = [];
}

/// <summary>
/// One track at one position in one playlist.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by its own <see cref="Id"/> rather than by (playlist, track). A playlist may
/// legitimately contain the same track twice -- a set closing with the track it opened
/// with, a study loop -- and a composite key would silently refuse the second copy.
/// It also means "remove this entry" is unambiguous when duplicates exist.
/// </para>
/// <para>
/// <see cref="Position"/> is a contiguous 0-based integer, renumbered on every mutation.
/// Sparse or fractional ordering avoids the renumbering, but costs a periodic
/// rebalance and makes "what is position 3" unanswerable without reading the whole list.
/// Playlists are small enough that rewriting them is cheaper than either.
/// </para>
/// </remarks>
public sealed class PlaylistItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid PlaylistId { get; set; }

    public Playlist? Playlist { get; set; }

    public Guid TrackId { get; set; }

    public Track? Track { get; set; }

    /// <summary>0-based, contiguous, and unique within the playlist.</summary>
    public int Position { get; set; }

    public DateTimeOffset AddedAtUtc { get; set; }
}
