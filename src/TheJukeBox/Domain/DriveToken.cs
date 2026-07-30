namespace TheJukeBox.Domain;

/// <summary>
/// The single Google Drive grant held for a user.
/// </summary>
/// <remarks>
/// <para>
/// One row per user, keyed by <see cref="UserId"/>: a second grant for the same
/// account is a replacement, not an addition, and making the primary key the foreign
/// key means the database enforces that rather than trusting application code -- the
/// bug the old <c>FileDataStore</c> keyed on the literal string <c>"user"</c> had in
/// the opposite direction, where every account shared one grant.
/// </para>
/// <para>
/// Only the refresh token is stored, and only encrypted. Access tokens are
/// deliberately absent: they live for an hour, are cheap to mint from the refresh
/// token, and persisting them would widen the blast radius of a database leak for no
/// benefit.
/// </para>
/// </remarks>
public sealed class DriveToken
{
    /// <summary>Primary key and foreign key to <see cref="Domain.User"/>.</summary>
    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>
    /// The refresh token, protected by
    /// <c>TheJukeBox.Infrastructure.Security.ITokenProtector</c>. Never assign a
    /// plaintext token to this property.
    /// </summary>
    public string EncryptedRefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// The space-separated scopes actually granted, as returned by Google.
    /// </summary>
    /// <remarks>
    /// Recorded because the user can decline individual scopes on the consent
    /// screen, so what we asked for and what we hold are not the same thing. A5
    /// checks this before starting a sync instead of discovering the shortfall as an
    /// opaque 403 from Drive.
    /// </remarks>
    public string Scopes { get; set; } = string.Empty;

    public DateTimeOffset GrantedAtUtc { get; set; }

    /// <summary>
    /// Set when the grant is known to be dead -- the user revoked it in their Google
    /// account, or Drive answered <c>invalid_grant</c>.
    /// </summary>
    public DateTimeOffset? RevokedAtUtc { get; set; }

    public DateTimeOffset? LastRefreshedAtUtc { get; set; }

    /// <summary>The last OAuth error code seen, e.g. <c>invalid_grant</c>.</summary>
    public string? LastErrorCode { get; set; }

    /// <summary>
    /// Whether this grant is worth attempting. Computed rather than stored so it can
    /// never disagree with <see cref="RevokedAtUtc"/>; explicitly unmapped in
    /// <c>DriveTokenConfiguration</c>.
    /// </summary>
    public bool IsUsable => RevokedAtUtc is null;
}
