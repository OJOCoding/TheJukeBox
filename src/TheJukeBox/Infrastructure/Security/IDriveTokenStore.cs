namespace TheJukeBox.Infrastructure.Security;

/// <summary>
/// Per-user Google Drive refresh tokens, encrypted at rest.
/// </summary>
/// <remarks>
/// This interface is the fix for the defect the whole revamp turns on. The previous
/// implementation persisted one token under the hardcoded key <c>"user"</c>, so there
/// was exactly one Drive grant for the entire instance and no identity to authorize
/// against: any caller who could reach the port got the owner's music. Keying tokens
/// by <see cref="Domain.User.Id"/> is what makes authorization possible at all.
/// </remarks>
public interface IDriveTokenStore
{
    /// <summary>
    /// Stores or replaces a user's refresh token, clearing any previous revocation.
    /// </summary>
    Task SaveAsync(Guid userId, string refreshToken, string scopes, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's decrypted refresh token, or <see langword="null"/> when absent,
    /// revoked, or undecryptable.
    /// </summary>
    /// <remarks>
    /// The three cases are deliberately collapsed: all of them mean "this user must
    /// reconnect their Drive", and distinguishing them at the call site would only
    /// invite different handling for outcomes that need the same response.
    /// </remarks>
    Task<string?> GetRefreshTokenAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a grant unusable, recording why.
    /// </summary>
    /// <remarks>
    /// Called on <c>invalid_grant</c>, which Google returns when the user revoked
    /// access, changed their password, or the token expired because the OAuth consent
    /// screen is still in Testing (where refresh tokens last only 7 days). None of
    /// those are retryable, so the grant is retired rather than hammered.
    /// </remarks>
    Task MarkRevokedAsync(Guid userId, string? errorCode, CancellationToken cancellationToken = default);

    /// <summary>Removes the grant entirely, for an explicit disconnect.</summary>
    Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default);
}
