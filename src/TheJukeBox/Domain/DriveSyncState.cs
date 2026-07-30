namespace TheJukeBox.Domain;

/// <summary>
/// The synchronisation cursor and progress for one user's library.
/// </summary>
/// <remarks>
/// One row per user, keyed by <see cref="UserId"/>, for the same reason as
/// <see cref="DriveToken"/>: two sync states for one account is a bug the schema
/// should make unrepresentable rather than a case the worker has to reconcile.
/// </remarks>
public sealed class DriveSyncState
{
    /// <summary>Primary key and foreign key to <see cref="Domain.User"/>.</summary>
    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>
    /// The Drive <c>changes.list</c> cursor. <see langword="null"/> means no
    /// incremental baseline exists and a full sync is required.
    /// </summary>
    /// <remarks>
    /// This single nullable string is the difference between C1's incremental sync
    /// costing one request per poll and costing a full re-listing of the library. It
    /// must only be advanced after the page it came from has been committed, or a
    /// crash between the two loses changes permanently and invisibly.
    /// </remarks>
    public string? ChangesStartPageToken { get; set; }

    public SyncStatus Status { get; set; } = SyncStatus.Idle;

    /// <summary>
    /// A short human-facing label for what is happening right now, e.g.
    /// <c>listing</c> or <c>reconciling</c>. Free-form on purpose: it is displayed,
    /// never branched on.
    /// </summary>
    public string? Phase { get; set; }

    public int ItemsProcessed { get; set; }

    /// <summary>
    /// Total items expected, or 0 while unknown. Drive does not report a count up
    /// front, so this stays 0 during the listing phase and the UI must render an
    /// indeterminate progress state rather than dividing by it.
    /// </summary>
    public int ItemsTotal { get; set; }

    public DateTimeOffset? LastFullSyncAtUtc { get; set; }

    public DateTimeOffset? LastIncrementalSyncAtUtc { get; set; }

    /// <summary>
    /// Consecutive failures, reset on success. Drives C1's quadratic backoff, and
    /// keeping the count in the database rather than in worker memory means a restart
    /// does not reset a hammering retry loop back to full speed.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// A stable classification of the last failure -- a <c>urn:jukebox:</c> problem
    /// type or an OAuth error code -- so the UI can distinguish "reconnect Drive"
    /// from "Google is rate limiting us" without parsing <see cref="LastError"/>.
    /// </summary>
    public string? LastErrorType { get; set; }

    /// <summary>
    /// Held by the worker currently syncing this user; expired or
    /// <see langword="null"/> means the row is free to claim.
    /// </summary>
    /// <remarks>
    /// A lease rather than a lock: a worker killed mid-sync cannot release anything,
    /// so ownership has to expire on its own or the user's library would be stuck
    /// until an operator intervened.
    /// </remarks>
    public DateTimeOffset? LeaseUntilUtc { get; set; }
}
