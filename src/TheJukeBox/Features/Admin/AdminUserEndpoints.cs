using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheJukeBox.Configuration;
using TheJukeBox.Diagnostics;
using TheJukeBox.Domain;
using TheJukeBox.Errors;
using TheJukeBox.Features.Auth;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Admin;

/// <summary>
/// Account administration: who is waiting, who is in, who is out.
/// </summary>
/// <remarks>
/// Without these, <see cref="RegistrationMode.AdminApproval"/> would be a door with no
/// handle -- accounts would queue up as pending and nothing could ever let them through.
/// </remarks>
public static class AdminUserEndpoints
{
    private const string LogCategory = "TheJukeBox.Features.Admin.AdminUserEndpoints";

    public static void MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes
            .MapGroup("/api/admin")
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithTags("Admin");

        group.MapGet("/users", (
            HttpContext httpContext,
            JukeBoxDbContext database,
            CancellationToken cancellationToken) =>
            ListAsync(httpContext.User.RequireUserId(), database, cancellationToken));

        group.MapGet("/registration", GetRegistration);

        group.MapPost("/users/{id:guid}/approve", (
            Guid id,
            HttpContext httpContext,
            JukeBoxDbContext database,
            IOptions<AuthOptions> authOptions,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            SetStatusAsync(
                id,
                UserStatus.Active,
                httpContext,
                database,
                authOptions.Value,
                loggerFactory,
                cancellationToken));

        group.MapPost("/users/{id:guid}/disable", (
            Guid id,
            HttpContext httpContext,
            JukeBoxDbContext database,
            IOptions<AuthOptions> authOptions,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            SetStatusAsync(
                id,
                UserStatus.Disabled,
                httpContext,
                database,
                authOptions.Value,
                loggerFactory,
                cancellationToken));
    }

    private static async Task<Ok<IReadOnlyList<AccountDto>>> ListAsync(
        Guid actorId,
        JukeBoxDbContext database,
        CancellationToken cancellationToken)
    {
        var accounts = await database.Users
            .AsNoTracking()

            // Pending first: this list exists to be acted on, and the accounts that need a
            // decision are the ones that should not have to be hunted for.
            .OrderBy(u => u.Status == UserStatus.Pending ? 0 : 1)
            .ThenBy(u => u.CreatedAtUtc)
            .Select(u => new AccountDto(
                u.Id,
                u.Email,
                u.DisplayName,
                u.PictureUrl,
                u.Status.ToString(),
                u.IsAdmin,
                u.CreatedAtUtc,
                u.LastSeenAtUtc,
                u.DriveToken != null && u.DriveToken.RevokedAtUtc == null,
                database.Tracks.Count(t => t.UserId == u.Id),
                u.Id == actorId))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok<IReadOnlyList<AccountDto>>(accounts);
    }

    private static Ok<RegistrationDto> GetRegistration(IOptions<AuthOptions> authOptions) =>
        TypedResults.Ok(new RegistrationDto(authOptions.Value.Registration.ToString()));

    private static async Task<Results<Ok<AccountStatusDto>, NotFound, ProblemHttpResult>> SetStatusAsync(
        Guid id,
        UserStatus status,
        HttpContext httpContext,
        JukeBoxDbContext database,
        AuthOptions authOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var actorId = httpContext.User.RequireUserId();

        var account = await database.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (account is null)
        {
            return TypedResults.NotFound();
        }

        // An administrator locking themselves out is unrecoverable without database access,
        // so it is refused rather than confirmed. Another administrator can still do it.
        if (account.Id == actorId && status == UserStatus.Disabled)
        {
            return Conflict(
                ProblemTypes.SelfDisable,
                "Cannot disable your own account.",
                "Disabling the account you are signed in with would lock you out of this "
                + "instance with no way back in. Ask another administrator, or promote one first.");
        }

        if (status == UserStatus.Active
            && !RegistrationPolicy.CanApprove(account.IsAdmin, authOptions.Registration))
        {
            // Approving under SingleUser would silently contradict the configuration. Fail
            // loudly and name the setting, rather than quietly doing it anyway.
            return Conflict(
                ProblemTypes.SingleUserInstance,
                "This instance is configured for a single user.",
                "Auth:Registration is SingleUser, so only the administrator account may be "
                + "active. Set it to AdminApproval and restart to approve other people.");
        }

        if (account.Status == status)
        {
            // Already there. Returning the current state keeps the call idempotent, which
            // matters because the admin UI can be double-clicked.
            return TypedResults.Ok(new AccountStatusDto(account.Id, account.Status.ToString()));
        }

        account.Status = status;

        // Any status change rotates the stamp, in both directions. On disable it is what
        // actually ends live sessions. On approve it is not strictly required, but a single
        // rule ("status changed, so old cookies are void") is easier to keep correct than
        // a conditional one.
        account.SecurityStamp = User.NewSecurityStamp();

        await database.SaveChangesAsync(cancellationToken);

        var logger = loggerFactory.CreateLogger(LogCategory);

        Log.AccountStatusChanged(logger, actorId, account.Id, status);

        return TypedResults.Ok(new AccountStatusDto(account.Id, account.Status.ToString()));
    }

    /// <summary>
    /// A 409 in the same RFC 9457 shape the exception handler produces, so the frontend has
    /// one response format to switch on rather than two.
    /// </summary>
    private static ProblemHttpResult Conflict(string type, string title, string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            title: title,
            type: type);
}

/// <param name="TrackCount">
/// Included because it is the one number that says whether an account is actually in use,
/// which is what makes "is this safe to disable" answerable from the list alone.
/// </param>
/// <param name="IsSelf">
/// Whether this row is the caller's own account. Computed here rather than by comparing ids
/// on the client, which would mean putting the session's user id into the bootstrap payload
/// for the sake of one button on one screen.
/// </param>
public sealed record AccountDto(
    Guid Id,
    string? Email,
    string? DisplayName,
    string? PictureUrl,
    string Status,
    bool IsAdmin,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    bool DriveConnected,
    int TrackCount,
    bool IsSelf);

public sealed record AccountStatusDto(Guid Id, string Status);

public sealed record RegistrationDto(string Mode);
