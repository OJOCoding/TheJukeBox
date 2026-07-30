namespace TheJukeBox.Domain;

/// <summary>
/// Where a user's library synchronisation currently stands.
/// </summary>
/// <remarks>
/// Persisted as its <em>name</em>, not its ordinal (see
/// <c>DriveSyncStateConfiguration</c>). Storing the ordinal would mean that
/// inserting a member in the middle of this enum silently reinterprets every
/// existing row, and it makes the table unreadable to anyone opening it with
/// <c>sqlite3</c> to diagnose a stuck sync -- which, for a self-hosted app, is the
/// realistic support channel.
/// </remarks>
public enum SyncStatus
{
    /// <summary>Nothing scheduled and nothing running.</summary>
    Idle = 0,

    /// <summary>Accepted and waiting for the worker.</summary>
    Queued = 1,

    /// <summary>A worker holds the lease and is making progress.</summary>
    Running = 2,

    /// <summary>The last run finished and the library is current.</summary>
    Succeeded = 3,

    /// <summary>The last run failed; see the recorded error and failure count.</summary>
    Failed = 4,
}
