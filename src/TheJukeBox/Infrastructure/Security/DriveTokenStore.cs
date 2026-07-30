using Microsoft.EntityFrameworkCore;
using TheJukeBox.Domain;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Infrastructure.Security;

/// <summary>
/// <see cref="IDriveTokenStore"/> over the database, with ciphertext at rest.
/// </summary>
internal sealed class DriveTokenStore(
    JukeBoxDbContext context,
    ITokenProtector protector) : IDriveTokenStore
{
    public async Task SaveAsync(
        Guid userId,
        string refreshToken,
        string scopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        var existing = await context.DriveTokens
            .FirstOrDefaultAsync(t => t.UserId == userId, cancellationToken);

        var ciphertext = protector.Protect(refreshToken);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            context.DriveTokens.Add(new DriveToken
            {
                UserId = userId,
                EncryptedRefreshToken = ciphertext,
                Scopes = scopes,
                GrantedAtUtc = now,
            });
        }
        else
        {
            existing.EncryptedRefreshToken = ciphertext;
            existing.Scopes = scopes;
            existing.GrantedAtUtc = now;

            // Re-consenting is how a user recovers from a revoked grant, so clear the
            // failure state rather than leaving the row looking broken.
            existing.RevokedAtUtc = null;
            existing.LastErrorCode = null;
            existing.LastRefreshedAtUtc = null;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetRefreshTokenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var token = await context.DriveTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.UserId == userId, cancellationToken);

        if (token is null || !token.IsUsable)
        {
            return null;
        }

        // Null here means the Data Protection key ring changed or the column is
        // corrupt. Treated as "no grant", which routes the caller to the reconnect
        // prompt instead of failing the request.
        return protector.Unprotect(token.EncryptedRefreshToken);
    }

    public async Task MarkRevokedAsync(
        Guid userId,
        string? errorCode,
        CancellationToken cancellationToken = default)
    {
        var token = await context.DriveTokens
            .FirstOrDefaultAsync(t => t.UserId == userId, cancellationToken);

        if (token is null || token.RevokedAtUtc is not null)
        {
            return;
        }

        token.RevokedAtUtc = DateTimeOffset.UtcNow;
        token.LastErrorCode = errorCode;

        // The ciphertext is left in place deliberately. It is already useless to
        // Google, and keeping it means a support question about when a grant died can
        // be answered from the row rather than guessed.
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await context.DriveTokens
            .Where(t => t.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
}
