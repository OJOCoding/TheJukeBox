namespace TheJukeBox.Domain;

/// <summary>
/// Where a user was in their listening, so a reload or a different device resumes it.
/// </summary>
/// <remarks>
/// <para>
/// One row per user, keyed by <see cref="UserId"/>. Rewritten in place rather than appended
/// to: this is a resume point, not a log, and the log is <see cref="PlayHistory"/>.
/// </para>
/// <para>
/// The queue is stored as a single opaque string of track ids rather than as a child table.
/// A child table would mean deleting and reinserting up to a thousand rows every time the
/// user skipped a track, and the queue is never queried by id -- it is written and read back
/// whole. Storing it as one value makes a save one row-write.
/// </para>
/// </remarks>
public sealed class PlaybackState
{
    /// <summary>Primary key. One row per user.</summary>
    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>
    /// The queued track ids in order, as 32-character hex with no separators.
    /// </summary>
    /// <remarks>
    /// Dash-free hex rather than JSON or a delimited list of formatted GUIDs: the format is
    /// fixed-width, so parsing is a chunked slice with nothing to escape and no delimiter a
    /// value could contain. At 32 bytes each, the cap below keeps the row under 64 KB.
    /// </remarks>
    public string QueuedTrackIds { get; set; } = string.Empty;

    /// <summary>
    /// How many ids <see cref="QueuedTrackIds"/> may hold.
    /// </summary>
    /// <remarks>
    /// A bound is needed because this row is rewritten every few seconds while music plays,
    /// and "shuffle my whole library" can queue tens of thousands of tracks. 2000 keeps the
    /// row at roughly 64 KB; past that, resuming the right *track* matters far more than
    /// preserving an exact tail nobody will reach.
    /// </remarks>
    public const int MaxQueueLength = 2000;

    /// <summary>Index into the play order, or -1 when nothing is cued.</summary>
    public int Cursor { get; set; } = -1;

    /// <summary>
    /// Playback offset within the current track, in seconds.
    /// </summary>
    /// <remarks>
    /// Stored so resuming a 90-minute mix does not start it again from the beginning. Not
    /// exact: it is written on a timer and when the page goes away, so a hard crash loses at
    /// most a few seconds.
    /// </remarks>
    public double PositionSeconds { get; set; }

    public bool Shuffle { get; set; }

    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>What happens at the end of the queue, or of a track.</summary>
public enum RepeatMode
{
    Off = 0,

    /// <summary>Wrap to the start of the queue.</summary>
    All = 1,

    /// <summary>Replay the current track.</summary>
    One = 2,
}
