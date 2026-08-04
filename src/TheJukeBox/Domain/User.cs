namespace TheJukeBox.Domain;

/// <summary>
/// A person with an account on this instance.
/// </summary>
/// <remarks>
/// <para>
/// The account key is <see cref="GoogleSubjectId"/>, never the email address.
/// Google's <c>sub</c> claim is documented as the only stable, never-reused
/// identifier for an account; an email address can be changed by its owner and, on
/// Workspace domains, can be reassigned to a different person entirely. Keying on
/// email would mean a rename silently creates a second library, and a reassignment
/// silently hands the first person's library to the second.
/// </para>
/// <para>
/// Everything else Google tells us about the account -- email, display name,
/// picture -- is descriptive only: refreshed opportunistically at sign-in, never
/// matched on, and safe to be stale or absent.
/// </para>
/// </remarks>
public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The Google <c>sub</c> claim. Unique, immutable, and the account key.</summary>
    public string GoogleSubjectId { get; set; } = string.Empty;

    /// <summary>Descriptive only. See the remarks on <see cref="User"/>.</summary>
    public string? Email { get; set; }

    /// <summary>Descriptive only.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Descriptive only: an opaque avatar URL served by Google.</summary>
    public string? PictureUrl { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastSeenAtUtc { get; set; }

    /// <summary>
    /// Administrators can see instance-wide diagnostics. A4 grants this to the first
    /// account that signs in, which for a self-hosted instance is the operator.
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Whether this account may use the instance.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="UserStatus.Pending"/> so a row created by any path that forgets
    /// to set it is closed rather than open.
    /// </remarks>
    public UserStatus Status { get; set; } = UserStatus.Pending;

    public bool IsActive => Status == UserStatus.Active;

    /// <summary>
    /// Changed to invalidate every existing session for this user.
    /// </summary>
    /// <remarks>
    /// The value is carried in the auth cookie and compared on each request, so
    /// bumping it is how "sign out everywhere" and "the Drive grant was revoked"
    /// take effect without a server-side session store. Defaulted here rather than
    /// left to callers so an account can never exist with an empty stamp, which
    /// would make every cookie for it trivially forgeable.
    /// </remarks>
    public string SecurityStamp { get; set; } = NewSecurityStamp();

    public DriveToken? DriveToken { get; set; }

    public DriveSyncState? SyncState { get; set; }

    /// <summary>A fresh 128-bit session-invalidation stamp.</summary>
    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");
}
