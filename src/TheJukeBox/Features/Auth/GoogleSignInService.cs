using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;
using TheJukeBox.Diagnostics;
using TheJukeBox.Domain;
using TheJukeBox.Infrastructure.Persistence;
using TheJukeBox.Infrastructure.Security;

namespace TheJukeBox.Features.Auth;

/// <summary>
/// Turns a successful Google sign-in into a local account and a session identity.
/// </summary>
internal sealed class GoogleSignInService(
    JukeBoxDbContext context,
    IDriveTokenStore tokenStore,
    ILogger<GoogleSignInService> logger)
{
    /// <summary>
    /// Upserts the account, stores the refresh token, and rewrites the identity so the
    /// session carries local claims rather than Google's.
    /// </summary>
    public async Task CompleteAsync(OAuthCreatingTicketContext ticketContext)
    {
        ArgumentNullException.ThrowIfNull(ticketContext);

        // Read Google's identifiers before touching the identity: the handler puts the
        // `sub` value in NameIdentifier, and that slot is about to be replaced with our
        // own user id.
        var googleSubject = ticketContext.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                            ?? throw new InvalidOperationException(
                                "Google returned no subject identifier; cannot establish an account.");

        var email = ticketContext.Principal.FindFirstValue(ClaimTypes.Email);
        var displayName = ticketContext.Principal.FindFirstValue(ClaimTypes.Name);
        var picture = ticketContext.Principal.FindFirstValue("urn:google:picture");

        var (user, isNew) = await UpsertAsync(googleSubject, email, displayName, picture);

        await StoreRefreshTokenAsync(user.Id, ticketContext);

        Rewrite(ticketContext.Identity, user, googleSubject);

        Log.SignedIn(logger, user.Id, googleSubject, isNew);
    }

    private async Task<(User User, bool IsNew)> UpsertAsync(
        string googleSubject,
        string? email,
        string? displayName,
        string? pictureUrl)
    {
        var now = DateTimeOffset.UtcNow;

        var user = await context.Users.FirstOrDefaultAsync(u => u.GoogleSubjectId == googleSubject);

        if (user is not null)
        {
            // Descriptive fields are refreshed on every sign-in because they drift:
            // people change their display name and their Google account email. The
            // subject id is what identifies the account, so none of this re-links
            // anything.
            user.Email = email;
            user.DisplayName = displayName;
            user.PictureUrl = pictureUrl;
            user.LastSeenAtUtc = now;

            await context.SaveChangesAsync();

            return (user, false);
        }

        user = new User
        {
            GoogleSubjectId = googleSubject,
            Email = email,
            DisplayName = displayName,
            PictureUrl = pictureUrl,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,

            // The first account to sign in owns the instance. Without this a freshly
            // deployed instance has no administrator, and the alternative (a bootstrap
            // password, or an env var) is more moving parts for a single-operator app.
            IsAdmin = !await context.Users.AnyAsync(),
        };

        context.Users.Add(user);
        context.DriveSyncStates.Add(new DriveSyncState { UserId = user.Id });

        await context.SaveChangesAsync();

        if (user.IsAdmin)
        {
            Log.FirstUserPromoted(logger, user.Id);
        }

        return (user, true);
    }

    private async Task StoreRefreshTokenAsync(Guid userId, OAuthCreatingTicketContext ticketContext)
    {
        var refreshToken = ticketContext.RefreshToken;

        if (string.IsNullOrEmpty(refreshToken))
        {
            // Google only issues a refresh token when it decides one is warranted. With
            // access_type=offline and prompt=consent it should always do so, but a user
            // who has already granted access and arrives without the consent prompt can
            // come back with an access token alone. Existing stored grants stay valid,
            // so this is a warning rather than a failure.
            Log.NoRefreshTokenIssued(logger, userId);
            return;
        }

        var scopes = ticketContext.TokenResponse?.Response?.RootElement
            .TryGetProperty("scope", out var scope) == true
            ? scope.GetString() ?? string.Empty
            : string.Empty;

        await tokenStore.SaveAsync(userId, refreshToken, scopes);
    }

    /// <summary>
    /// Replaces the Google-issued claims with this application's own.
    /// </summary>
    /// <remarks>
    /// Nothing from Google is left on the principal that an authorization decision
    /// could accidentally key on. In particular <see cref="ClaimTypes.NameIdentifier"/>
    /// becomes the local user id, because every ownership check reads it.
    /// </remarks>
    private static void Rewrite(ClaimsIdentity? identity, User user, string googleSubject)
    {
        if (identity is null)
        {
            return;
        }

        foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(JukeBoxClaims.GoogleSubject, googleSubject));
        identity.AddClaim(new Claim(JukeBoxClaims.SecurityStamp, user.SecurityStamp));

        if (user.IsAdmin)
        {
            identity.AddClaim(new Claim(JukeBoxClaims.IsAdmin, "true"));
        }
    }
}
