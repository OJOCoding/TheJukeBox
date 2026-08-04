using Microsoft.AspNetCore.Authorization;
using TheJukeBox.Domain;

namespace TheJukeBox.Features.Auth;

/// <summary>
/// The authorization policies for this instance.
/// </summary>
/// <remarks>
/// <para>
/// Being signed in and being allowed in are different things once registration is
/// controlled: anyone with a Google account can complete sign-in, so the account is
/// authenticated the moment it exists, while approval is a separate decision the operator
/// makes.
/// </para>
/// <para>
/// Both the default and the fallback policy therefore require an approved account. The
/// default matters more than it looks: every content endpoint is written as a bare
/// <c>RequireAuthorization()</c>, which binds to the default policy, so gating only the
/// fallback would have left the entire library reachable by unapproved accounts while
/// appearing to be protected.
/// </para>
/// </remarks>
public static class AuthorizationPolicies
{
    /// <summary>Signed in, but not necessarily approved. For sign-out and self-inspection.</summary>
    public const string SignedIn = "SignedIn";

    /// <summary>Signed in and approved. The default and fallback policy.</summary>
    public const string ActiveUser = "ActiveUser";

    /// <summary>An approved administrator.</summary>
    public const string Administrator = "Administrator";

    public static IServiceCollection AddJukeBoxAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var signedIn = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        var activeUser = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(JukeBoxClaims.Status, nameof(UserStatus.Active))
            .Build();

        var administrator = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(JukeBoxClaims.Status, nameof(UserStatus.Active))
            .RequireClaim(JukeBoxClaims.IsAdmin, "true")
            .Build();

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(activeUser)
            .SetFallbackPolicy(activeUser)
            .AddPolicy(SignedIn, signedIn)
            .AddPolicy(ActiveUser, activeUser)
            .AddPolicy(Administrator, administrator);

        return services;
    }
}
