using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using TheJukeBox.Diagnostics;
using TheJukeBox.Domain;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Auth;

/// <summary>
/// Re-checks the signed-in account against the database on each request.
/// </summary>
/// <remarks>
/// <para>
/// A cookie is a snapshot of who someone was when they signed in. This instance issues them
/// for two weeks, so without revalidation "disable this account" would mean "disable it in up
/// to fourteen days", which is not a control at all. The same applies in the other direction:
/// approving a pending user would have no effect until they happened to sign in again.
/// </para>
/// <para>
/// The cost is one indexed primary-key lookup per request. That is worth paying to make
/// approval and revocation immediate; caching it would reintroduce exactly the staleness this
/// exists to remove.
/// </para>
/// </remarks>
internal static class UserSessionValidator
{
    private const string LogCategory = "TheJukeBox.Features.Auth.UserSessionValidator";

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var principal = context.Principal;
        var userId = principal?.UserId();

        if (userId is null)
        {
            await RejectAsync(context);
            return;
        }

        var services = context.HttpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
        var database = services.GetRequiredService<JukeBoxDbContext>();

        var account = await database.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Status, u.SecurityStamp, u.IsAdmin })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        // The row is gone: the account was deleted while a session was live.
        if (account is null)
        {
            await RejectAsync(context);
            return;
        }

        // The stamp is the revocation lever. Rotating it invalidates every existing cookie for
        // that account at once, which is what makes "sign out everywhere" possible.
        if (!string.Equals(
                principal!.FindFirstValue(JukeBoxClaims.SecurityStamp),
                account.SecurityStamp,
                StringComparison.Ordinal))
        {
            Log.SessionRejected(logger, userId.Value, "security stamp changed");

            await RejectAsync(context);
            return;
        }

        if (account.Status == UserStatus.Disabled)
        {
            Log.SessionRejected(logger, userId.Value, "account disabled");

            await RejectAsync(context);
            return;
        }

        // Status and admin flag are refreshed rather than trusted, so an approval made a second
        // ago is honoured on this request.
        Refresh(context, JukeBoxClaims.Status, account.Status.ToString());
        Refresh(context, JukeBoxClaims.IsAdmin, account.IsAdmin ? "true" : null);
    }

    /// <summary>
    /// Replaces a claim's value on the current principal.
    /// </summary>
    /// <remarks>
    /// Only the in-memory principal is changed; <c>ShouldRenew</c> is left alone, so the cookie
    /// is not rewritten on every request just to carry a value that is re-derived anyway.
    /// </remarks>
    private static void Refresh(CookieValidatePrincipalContext context, string claimType, string? value)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        foreach (var existing in identity.FindAll(claimType).ToArray())
        {
            identity.RemoveClaim(existing);
        }

        if (value is not null)
        {
            identity.AddClaim(new Claim(claimType, value));
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();

        // Actively deleted, not merely ignored. Leaving the cookie in place means the browser
        // re-sends a known-bad credential on every subsequent request, and awaiting this is
        // what guarantees the deletion header is written before the response starts.
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
