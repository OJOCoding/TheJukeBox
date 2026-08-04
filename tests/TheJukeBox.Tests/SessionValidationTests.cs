using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheJukeBox.Domain;
using TheJukeBox.Features.Auth;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Tests;

/// <summary>
/// The revalidation hook, driven the way the cookie handler drives it.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes approval and revocation immediate. Sessions here last fourteen days
/// with sliding expiration, so if the cookie were trusted as issued, "disable this account"
/// would mean "disable it within a fortnight" and approving a pending account would do
/// nothing until the user happened to sign in again.
/// </para>
/// <para>
/// Exercised through a real <see cref="CookieValidatePrincipalContext"/> against a real
/// migrated database. Calling a hand-rolled extraction of the logic instead would prove the
/// logic and not the hook, and the hook not being wired up is the more likely mistake.
/// </para>
/// </remarks>
public class SessionValidationTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    [Fact]
    public async Task An_active_account_keeps_its_session()
    {
        var user = await SeedAsync(UserStatus.Active);
        var context = ContextFor(user);

        await UserSessionValidator.ValidateAsync(context);

        Assert.NotNull(context.Principal);
        Assert.Equal(UserStatus.Active, context.Principal!.StatusOf());
    }

    /// <summary>
    /// Disabling an account has to end the sessions it already has. Rejecting only future
    /// sign-ins would leave the person with full access for as long as their cookie lived.
    /// </summary>
    [Fact]
    public async Task A_disabled_account_is_rejected_and_signed_out()
    {
        var user = await SeedAsync(UserStatus.Active);
        var context = ContextFor(user);

        await SetStatusAsync(user.Id, UserStatus.Disabled);

        await UserSessionValidator.ValidateAsync(context);

        Assert.Null(context.Principal);
        Assert.True(SignOutRecorder.Of(context).SignedOut);
    }

    /// <summary>
    /// The other direction, and the one an operator notices: an approval taken a moment ago
    /// must be honoured on the very next request, without the user signing in again.
    /// </summary>
    [Fact]
    public async Task An_approval_is_reflected_on_the_next_request()
    {
        var user = await SeedAsync(UserStatus.Pending);
        var context = ContextFor(user);

        // The cookie still says Pending, because it was issued before the approval.
        Assert.Equal(UserStatus.Pending, context.Principal!.StatusOf());

        await SetStatusAsync(user.Id, UserStatus.Active);

        await UserSessionValidator.ValidateAsync(context);

        Assert.NotNull(context.Principal);
        Assert.Equal(UserStatus.Active, context.Principal!.StatusOf());
    }

    /// <summary>
    /// A pending account stays authenticated rather than being signed out. It has to: the
    /// welcome view needs a session to be able to say "waiting for approval", and the person
    /// needs one to be able to sign out again.
    /// </summary>
    [Fact]
    public async Task A_pending_account_keeps_its_session_but_stays_pending()
    {
        var user = await SeedAsync(UserStatus.Pending);
        var context = ContextFor(user);

        await UserSessionValidator.ValidateAsync(context);

        Assert.NotNull(context.Principal);
        Assert.Equal(UserStatus.Pending, context.Principal!.StatusOf());
        Assert.False(SignOutRecorder.Of(context).SignedOut);
    }

    /// <summary>
    /// The stamp is the "sign out everywhere" lever, and rotating it is what every status
    /// change does. Without this check, a rotated stamp would mean nothing.
    /// </summary>
    [Fact]
    public async Task A_stale_security_stamp_is_rejected()
    {
        var user = await SeedAsync(UserStatus.Active);
        var context = ContextFor(user);

        await using (var write = database.CreateContext())
        {
            var row = await write.Users.SingleAsync(u => u.Id == user.Id);
            row.SecurityStamp = User.NewSecurityStamp();
            await write.SaveChangesAsync();
        }

        await UserSessionValidator.ValidateAsync(context);

        Assert.Null(context.Principal);
        Assert.True(SignOutRecorder.Of(context).SignedOut);
    }

    [Fact]
    public async Task A_deleted_account_is_rejected()
    {
        var user = await SeedAsync(UserStatus.Active);
        var context = ContextFor(user);

        await using (var write = database.CreateContext())
        {
            write.Users.Remove(await write.Users.SingleAsync(u => u.Id == user.Id));
            await write.SaveChangesAsync();
        }

        await UserSessionValidator.ValidateAsync(context);

        Assert.Null(context.Principal);
    }

    /// <summary>
    /// A cookie with no user id cannot be checked against anything, so it is rejected rather
    /// than allowed through unverified.
    /// </summary>
    [Fact]
    public async Task A_principal_with_no_user_id_is_rejected()
    {
        var context = ContextFor(principal: new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, "nobody@example.com")],
            CookieAuthenticationDefaults.AuthenticationScheme)));

        await UserSessionValidator.ValidateAsync(context);

        Assert.Null(context.Principal);
    }

    /// <summary>
    /// The admin flag is refreshed from the row too, so demoting someone takes effect at once
    /// and does not depend on their cookie expiring.
    /// </summary>
    [Fact]
    public async Task A_revoked_admin_flag_is_dropped_from_the_principal()
    {
        var user = await SeedAsync(UserStatus.Active, isAdmin: true);
        var context = ContextFor(user);

        Assert.True(context.Principal!.IsAdministrator());

        await using (var write = database.CreateContext())
        {
            var row = await write.Users.SingleAsync(u => u.Id == user.Id);
            row.IsAdmin = false;
            await write.SaveChangesAsync();
        }

        await UserSessionValidator.ValidateAsync(context);

        Assert.NotNull(context.Principal);
        Assert.False(context.Principal!.IsAdministrator());
    }

    private async Task<User> SeedAsync(UserStatus status, bool isAdmin = false)
    {
        await using var context = database.CreateContext();

        var user = new User
        {
            GoogleSubjectId = $"subject-{Guid.NewGuid():N}",
            Email = "listener@example.com",
            DisplayName = "Listener",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow,
            Status = status,
            IsAdmin = isAdmin,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user;
    }

    private async Task SetStatusAsync(Guid userId, UserStatus status)
    {
        await using var context = database.CreateContext();

        var user = await context.Users.SingleAsync(u => u.Id == userId);
        user.Status = status;

        await context.SaveChangesAsync();
    }

    /// <summary>A context carrying the cookie an account would have been issued.</summary>
    private CookieValidatePrincipalContext ContextFor(User user) =>
        ContextFor(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(JukeBoxClaims.SecurityStamp, user.SecurityStamp),
                new Claim(JukeBoxClaims.Status, user.Status.ToString()),
                .. user.IsAdmin ? new[] { new Claim(JukeBoxClaims.IsAdmin, "true") } : [],
            ],
            CookieAuthenticationDefaults.AuthenticationScheme)));

    private CookieValidatePrincipalContext ContextFor(ClaimsPrincipal principal)
    {
        var services = new ServiceCollection();

        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(database.CreateContext());
        services.AddSingleton<IAuthenticationService, SignOutRecorder>();

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(CookieAuthenticationHandler));

        return new CookieValidatePrincipalContext(
            httpContext,
            scheme,
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

/// <summary>
/// That the hook is actually attached to the cookie handler.
/// </summary>
/// <remarks>
/// Every test above calls <c>ValidateAsync</c> directly, so all of them would still pass if
/// the <c>OnValidatePrincipal</c> assignment were deleted -- and the application would then
/// trust cookies for fourteen days with no revalidation at all. This is the assertion that
/// closes that gap.
/// </remarks>
public class SessionValidationWiringTests
{
    [Fact]
    public void The_cookie_handler_revalidates_every_request()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddJukeBoxAuthentication(
            new TheJukeBox.Configuration.AppOptions(),
            new TheJukeBox.Configuration.GoogleOAuthOptions
            {
                ClientId = "test-client-id",
                ClientSecret = "test-client-secret",
            });

        using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        // Compared as a method group rather than merely checked for non-null: the default
        // OnValidatePrincipal is a no-op delegate, not null, so a null check would pass
        // against an unwired handler.
        Assert.Equal(
            new Func<CookieValidatePrincipalContext, Task>(UserSessionValidator.ValidateAsync),
            options.Events.OnValidatePrincipal);
    }
}

/// <summary>
/// Records that the validator asked for a sign-out.
/// </summary>
/// <remarks>
/// RejectPrincipal alone only makes the current request anonymous; the cookie stays in the
/// browser and is re-sent on every subsequent request. Asserting the sign-out happened is
/// what distinguishes "this request was refused" from "this credential was revoked".
/// </remarks>
internal sealed class SignOutRecorder : IAuthenticationService
{
    public bool SignedOut { get; private set; }

    public static SignOutRecorder Of(CookieValidatePrincipalContext context) =>
        (SignOutRecorder)context.HttpContext.RequestServices.GetRequiredService<IAuthenticationService>();

    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        SignedOut = true;
        return Task.CompletedTask;
    }

    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
        Task.FromResult(AuthenticateResult.NoResult());

    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
        Task.CompletedTask;

    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
        Task.CompletedTask;

    public Task SignInAsync(
        HttpContext context,
        string? scheme,
        ClaimsPrincipal principal,
        AuthenticationProperties? properties) =>
        Task.CompletedTask;
}
