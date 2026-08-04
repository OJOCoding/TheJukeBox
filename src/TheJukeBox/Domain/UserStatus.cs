namespace TheJukeBox.Domain;

/// <summary>
/// Whether an account may use this instance.
/// </summary>
/// <remarks>
/// Persisted as its name rather than its ordinal, like every other enum here, so inserting a
/// member cannot silently reinterpret existing rows and the table stays readable by hand.
/// </remarks>
public enum UserStatus
{
    /// <summary>
    /// Signed in, but not yet allowed to do anything.
    /// </summary>
    /// <remarks>
    /// The default for a new account when the instance requires approval. A pending user has a
    /// real session and a real row, which matters: it means the administrator can see who is
    /// asking, and the person is told they are waiting rather than being shown a broken app.
    /// </remarks>
    Pending = 0,

    /// <summary>Approved. Ordinary use.</summary>
    Active = 1,

    /// <summary>
    /// Revoked. Kept rather than deleted so their library rows, and the record of who they
    /// were, survive; deleting the row would cascade the whole library away.
    /// </summary>
    Disabled = 2,
}
