using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace TheJukeBox.Features.Auth;

/// <summary>
/// The claims this application puts in its own session cookie, and how to read them.
/// </summary>
public static class JukeBoxClaims
{
    /// <summary>The Google <c>sub</c> value, kept for diagnostics and re-linking.</summary>
    public const string GoogleSubject = "jukebox:google_sub";

    /// <summary>Stamped so a disabled account's existing cookies can be rejected.</summary>
    public const string SecurityStamp = "jukebox:security_stamp";

    public const string IsAdmin = "jukebox:is_admin";

    /// <summary>
    /// The local <see cref="Domain.User.Id"/> of the signed-in user.
    /// </summary>
    /// <remarks>
    /// Held in <see cref="ClaimTypes.NameIdentifier"/>. Deliberately our own key rather
    /// than Google's subject id: every ownership check joins on this, and putting a
    /// third party's identifier on that path would mean a Google-side change could
    /// orphan a library.
    /// </remarks>
    public static Guid? UserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// The signed-in user's id, or throws. For use only where authorization has
    /// already established that there is one.
    /// </summary>
    public static Guid RequireUserId(this ClaimsPrincipal principal) =>
        principal.UserId()
        ?? throw new InvalidOperationException(
            "No authenticated user id on the principal. The endpoint is missing RequireAuthorization().");

    public static bool TryGetUserId(this ClaimsPrincipal principal, [NotNullWhen(true)] out Guid? userId)
    {
        userId = principal.UserId();
        return userId is not null;
    }

    public static bool IsAdministrator(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return string.Equals(principal.FindFirstValue(IsAdmin), "true", StringComparison.Ordinal);
    }
}
