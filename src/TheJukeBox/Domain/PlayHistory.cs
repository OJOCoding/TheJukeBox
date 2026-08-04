namespace TheJukeBox.Domain;

/// <summary>
/// One recorded listen.
/// </summary>
/// <remarks>
/// <para>
/// Append-only, and deliberately separate from <see cref="PlaybackState"/>: one is a log,
/// the other is a resume point, and conflating them would mean either losing history on
/// every skip or rewriting a growing row on every save.
/// </para>
/// <para>
/// A row is written once per listen, not once per <c>play()</c> call. What counts as a
/// listen is decided by the client -- see <c>MinimumSeconds</c> -- because only the client
/// knows how much audio actually reached the speakers.
/// </para>
/// <para>
/// Pruned, not kept forever. See <see cref="MaxRowsPerUser"/>.
/// </para>
/// </remarks>
public sealed class PlayHistory
{
    /// <summary>
    /// The shortest listen worth recording, in seconds.
    /// </summary>
    /// <remarks>
    /// Below this it is a skip, and logging skips would make "recently played" a list of
    /// things the user rejected. Twenty seconds is long enough to exclude flicking through
    /// a queue and short enough to include a track someone cut off deliberately.
    /// </remarks>
    public const int MinimumSeconds = 20;

    /// <summary>
    /// How many rows a user keeps before the oldest are pruned.
    /// </summary>
    /// <remarks>
    /// Unbounded history is a slow leak in a self-hosted database nobody administers: at a
    /// few hundred plays a week it is not a problem this year and is a problem eventually.
    /// Five thousand rows is years of listening and a few hundred kilobytes.
    /// </remarks>
    public const int MaxRowsPerUser = 5000;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public User? User { get; set; }

    public Guid TrackId { get; set; }

    public Track? Track { get; set; }

    public DateTimeOffset PlayedAtUtc { get; set; }

    /// <summary>How much of the track was actually played, in seconds.</summary>
    public int SecondsPlayed { get; set; }

    /// <summary>
    /// Whether playback reached the end of the track.
    /// </summary>
    /// <remarks>
    /// Distinguishes "listened to this" from "listened to a bit of this and moved on", which
    /// is the difference between a favourite and a mistake.
    /// </remarks>
    public bool Completed { get; set; }
}
