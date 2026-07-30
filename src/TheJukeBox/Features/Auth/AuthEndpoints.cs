using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TheJukeBox.Infrastructure.Persistence;
using TheJukeBox.Infrastructure.Security;

namespace TheJukeBox.Features.Auth;

/// <summary>
/// Sign-in, sign-out, and the single call the frontend boots from.
/// </summary>
/// <remarks>
/// Replaces the previous <c>AuthController</c>, which hand-rolled the authorization
/// URL with no <c>state</c> parameter (so the callback accepted a code from anyone),
/// exchanged codes itself, and rendered HTML containing the raw <c>code</c> query
/// value. The framework handler supplies <c>state</c> and PKCE, and there is no
/// hand-written HTML left to inject into.
/// </remarks>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/auth").WithTags("Auth");

        // Anonymous: this is how a signed-out visitor starts.
        group.MapGet("/login", StartSignIn).AllowAnonymous();

        // Handlers take more than a lone HttpContext deliberately. A delegate shaped
        // Func<HttpContext, Task<T>> is convertible to RequestDelegate, overload
        // resolution prefers that overload, and the framework then discards the result
        // instead of writing it (analyzer ASP0016). Taking the service explicitly avoids
        // the collision without inventing an unused parameter.
        group.MapPost("/logout", SignOutAsync).RequireAuthorization();

        group.MapGet("/me", (HttpContext httpContext) => Me(httpContext))
            .RequireAuthorization();

        group.MapPost("/drive/disconnect", (
                HttpContext httpContext,
                IDriveTokenStore tokenStore,
                CancellationToken cancellationToken) =>
                DisconnectDriveAsync(httpContext, tokenStore, cancellationToken))
            .RequireAuthorization();
    }

    /// <summary>
    /// The bootstrap call. One request decides which view the shell shows.
    /// </summary>
    /// <remarks>
    /// Anonymous and always 200, unlike <c>/api/auth/me</c> which is 401 when signed
    /// out. The shell needs to distinguish "signed out" from "request failed" before it
    /// can render anything, and a single always-succeeding call avoids both a flash of
    /// the wrong state and three serialised requests before first paint.
    /// </remarks>
    public static void MapSessionEndpoint(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/api/session", async (
            HttpContext httpContext,
            JukeBoxDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (!httpContext.User.TryGetUserId(out var userId))
            {
                return Results.Ok(new SessionDto(false, null, false, false));
            }

            var snapshot = await database.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new
                {
                    u.DisplayName,
                    u.Email,
                    u.PictureUrl,
                    u.IsAdmin,
                    DriveConnected = u.DriveToken != null && u.DriveToken.RevokedAtUtc == null,
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (snapshot is null)
            {
                // The cookie outlived the account: the row was deleted while a session
                // was live. Treat it as signed out rather than 500.
                return Results.Ok(new SessionDto(false, null, false, false));
            }

            return Results.Ok(new SessionDto(
                Authenticated: true,
                User: new UserDto(snapshot.DisplayName, snapshot.Email, snapshot.PictureUrl, snapshot.IsAdmin),
                DriveConnected: snapshot.DriveConnected,
                IsAdmin: snapshot.IsAdmin));
        }).AllowAnonymous().WithTags("Auth");
    }

    /// <summary>
    /// Redirects to Google, remembering where the user was heading.
    /// </summary>
    /// <remarks>
    /// A top-level navigation, not something callable by <c>fetch</c>: Google refuses to
    /// render its consent screen in an iframe, and OAuth needs a real browser redirect.
    /// </remarks>
    private static ChallengeHttpResult StartSignIn(string? returnUrl) =>
        TypedResults.Challenge(
            new AuthenticationProperties { RedirectUri = SafeReturnUrl.Sanitise(returnUrl) },
            [GoogleDefaults.AuthenticationScheme]);

    private static async Task<NoContent> SignOutAsync(
        HttpContext httpContext,
        IAuthenticationService authentication)
    {
        // Only the local session cookie is dropped. The Drive grant deliberately
        // survives, so signing back in does not require re-consenting; "forget my
        // Drive" is the separate, explicit disconnect below.
        await authentication.SignOutAsync(
            httpContext,
            CookieAuthenticationDefaults.AuthenticationScheme,
            properties: null);

        return TypedResults.NoContent();
    }

    private static Ok<UserDto> Me(HttpContext httpContext) =>
        TypedResults.Ok(new UserDto(
            httpContext.User.Identity?.Name,
            httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
            httpContext.User.FindFirst("urn:google:picture")?.Value,
            httpContext.User.IsAdministrator()));

    private static async Task<NoContent> DisconnectDriveAsync(
        HttpContext httpContext,
        IDriveTokenStore tokenStore,
        CancellationToken cancellationToken)
    {
        await tokenStore.DeleteAsync(httpContext.User.RequireUserId(), cancellationToken);

        return TypedResults.NoContent();
    }
}

/// <summary>What the shell needs before it can render.</summary>
public sealed record SessionDto(bool Authenticated, UserDto? User, bool DriveConnected, bool IsAdmin);

public sealed record UserDto(string? DisplayName, string? Email, string? PictureUrl, bool IsAdmin);
