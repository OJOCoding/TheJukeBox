using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TheJukeBox.Configuration;
using TheJukeBox.Domain;
using TheJukeBox.Features.Auth;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Tests;

public class RegistrationPolicyTests
{
    /// <summary>
    /// The instance-bricking case. Nobody can approve the first account but itself, so if it
    /// ever started pending the instance would have no administrator and no route to one.
    /// </summary>
    [Theory]
    [InlineData(RegistrationMode.AdminApproval)]
    [InlineData(RegistrationMode.Open)]
    [InlineData(RegistrationMode.SingleUser)]
    public void The_first_account_is_always_active(RegistrationMode mode) =>
        Assert.Equal(
            UserStatus.Active,
            RegistrationPolicy.StatusForNewAccount(isFirstAccount: true, mode));

    [Theory]
    [InlineData(RegistrationMode.AdminApproval, UserStatus.Pending)]
    [InlineData(RegistrationMode.SingleUser, UserStatus.Pending)]
    [InlineData(RegistrationMode.Open, UserStatus.Active)]
    public void Later_accounts_follow_the_mode(RegistrationMode mode, UserStatus expected) =>
        Assert.Equal(expected, RegistrationPolicy.StatusForNewAccount(isFirstAccount: false, mode));

    /// <summary>
    /// A configuration typo, or a mode added by a later version and read by an older binary,
    /// must not fail open. This is the one case where the switch's default arm matters.
    /// </summary>
    [Fact]
    public void An_unrecognised_mode_is_treated_as_requiring_approval() =>
        Assert.Equal(
            UserStatus.Pending,
            RegistrationPolicy.StatusForNewAccount(isFirstAccount: false, (RegistrationMode)999));

    [Fact]
    public void The_default_mode_requires_approval() =>
        Assert.Equal(RegistrationMode.AdminApproval, new AuthOptions().Registration);

    /// <summary>
    /// Defaulting the property to Pending is what makes "any code path that forgets to set a
    /// status produces a closed account" true rather than merely intended.
    /// </summary>
    [Fact]
    public void A_user_constructed_without_a_status_is_not_active()
    {
        var user = new User();

        Assert.Equal(UserStatus.Pending, user.Status);
        Assert.False(user.IsActive);
    }

    [Theory]
    [InlineData(RegistrationMode.SingleUser, false, false)]
    [InlineData(RegistrationMode.SingleUser, true, true)]
    [InlineData(RegistrationMode.AdminApproval, false, true)]
    [InlineData(RegistrationMode.Open, false, true)]
    public void Approval_is_refused_for_extra_accounts_on_a_single_user_instance(
        RegistrationMode mode,
        bool isAdminAccount,
        bool expected) =>
        Assert.Equal(expected, RegistrationPolicy.CanApprove(isAdminAccount, mode));
}

public class StatusClaimTests
{
    /// <summary>
    /// A cookie issued before this claim existed still authenticates. Reading its absence as
    /// Active would hand every pre-upgrade session an unapproved pass; reading it as Pending
    /// costs the user one sign-in.
    /// </summary>
    [Fact]
    public void A_principal_with_no_status_claim_is_pending() =>
        Assert.Equal(UserStatus.Pending, Principal().StatusOf());

    [Theory]
    [InlineData("Active", UserStatus.Active)]
    [InlineData("Pending", UserStatus.Pending)]
    [InlineData("Disabled", UserStatus.Disabled)]
    public void A_status_claim_round_trips(string claimValue, UserStatus expected) =>
        Assert.Equal(expected, Principal((JukeBoxClaims.Status, claimValue)).StatusOf());

    [Theory]
    [InlineData("")]
    [InlineData("active")]
    [InlineData("Approved")]
    [InlineData("1")]
    public void An_unparseable_status_claim_is_pending(string claimValue) =>
        Assert.Equal(UserStatus.Pending, Principal((JukeBoxClaims.Status, claimValue)).StatusOf());

    internal static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)),
            CookieAuthenticationDefaults.AuthenticationScheme));
}

/// <summary>
/// The gate itself, exercised through the real authorization service.
/// </summary>
/// <remarks>
/// Asserting on the policy objects' requirement lists would pass while the policies were
/// never registered, or registered under a name nothing uses. Resolving
/// <see cref="IAuthorizationService"/> from a container built by the shipped registration
/// method tests the wiring as well as the intent.
/// </remarks>
public sealed class AuthorizationPolicyTests : IDisposable
{
    private readonly ServiceProvider _services;

    public AuthorizationPolicyTests()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddJukeBoxAuthorization();

        _services = services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(AuthorizationPolicies.ActiveUser, false)]
    [InlineData(AuthorizationPolicies.Administrator, false)]
    [InlineData(AuthorizationPolicies.SignedIn, true)]
    public async Task A_pending_account_is_signed_in_but_not_admitted(string policy, bool expected)
    {
        var principal = StatusClaimTests.Principal(
            (ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            (JukeBoxClaims.Status, nameof(UserStatus.Pending)));

        Assert.Equal(expected, await SucceedsAsync(principal, policy));
    }

    [Fact]
    public async Task A_disabled_account_is_not_admitted()
    {
        var principal = StatusClaimTests.Principal(
            (ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            (JukeBoxClaims.Status, nameof(UserStatus.Disabled)));

        Assert.False(await SucceedsAsync(principal, AuthorizationPolicies.ActiveUser));
    }

    [Fact]
    public async Task An_active_account_is_admitted_but_is_not_an_administrator()
    {
        var principal = StatusClaimTests.Principal(
            (ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            (JukeBoxClaims.Status, nameof(UserStatus.Active)));

        Assert.True(await SucceedsAsync(principal, AuthorizationPolicies.ActiveUser));
        Assert.False(await SucceedsAsync(principal, AuthorizationPolicies.Administrator));
    }

    /// <summary>
    /// Admin is approval AND the flag, not either. A disabled administrator keeping access to
    /// the screen that re-enables accounts would defeat the point of disabling them.
    /// </summary>
    [Fact]
    public async Task A_pending_administrator_is_not_admitted_to_admin()
    {
        var principal = StatusClaimTests.Principal(
            (ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            (JukeBoxClaims.Status, nameof(UserStatus.Pending)),
            (JukeBoxClaims.IsAdmin, "true"));

        Assert.False(await SucceedsAsync(principal, AuthorizationPolicies.Administrator));
    }

    [Fact]
    public async Task An_active_administrator_is_admitted_to_admin()
    {
        var principal = StatusClaimTests.Principal(
            (ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            (JukeBoxClaims.Status, nameof(UserStatus.Active)),
            (JukeBoxClaims.IsAdmin, "true"));

        Assert.True(await SucceedsAsync(principal, AuthorizationPolicies.Administrator));
    }

    /// <summary>
    /// Every content endpoint is a bare <c>RequireAuthorization()</c>, which binds to the
    /// default policy rather than the fallback. Gating only the fallback would have left the
    /// whole library open to unapproved accounts while looking protected.
    /// </summary>
    [Fact]
    public async Task The_default_and_fallback_policies_both_require_approval()
    {
        var options = _services.GetRequiredService<
            IOptions<AuthorizationOptions>>().Value;

        var pending = StatusClaimTests.Principal(
            (ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            (JukeBoxClaims.Status, nameof(UserStatus.Pending)));

        var authorization = _services.GetRequiredService<IAuthorizationService>();

        Assert.NotNull(options.FallbackPolicy);
        Assert.False((await authorization.AuthorizeAsync(pending, null, options.DefaultPolicy)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(pending, null, options.FallbackPolicy!)).Succeeded);
    }

    [Fact]
    public async Task An_anonymous_caller_is_not_admitted()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(await SucceedsAsync(anonymous, AuthorizationPolicies.SignedIn));
        Assert.False(await SucceedsAsync(anonymous, AuthorizationPolicies.ActiveUser));
    }

    public void Dispose() => _services.Dispose();

    private async Task<bool> SucceedsAsync(ClaimsPrincipal principal, string policy)
    {
        var authorization = _services.GetRequiredService<IAuthorizationService>();

        return (await authorization.AuthorizeAsync(principal, resource: null, policy)).Succeeded;
    }
}

/// <summary>
/// The upgrade path, on its own database.
/// </summary>
/// <remarks>
/// Cannot share <see cref="SqliteDatabase"/>: that fixture arrives fully migrated, and the
/// whole point here is to start one migration short of the head.
/// </remarks>
public sealed class AccountStatusMigrationTests : IDisposable
{
    private const string MigrationUnderTest = "AccountStatus";

    private readonly string _directory;
    private readonly string _databaseFile;

    public AccountStatusMigrationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "jukebox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databaseFile = Path.Combine(_directory, "jukebox.db");
    }

    /// <summary>
    /// The failure this exists to prevent: EF scaffolds a non-null column with an empty-string
    /// default, so without a backfill every account that existed before the upgrade would come
    /// back with a status that is not a <see cref="UserStatus"/> at all -- locking out every
    /// user, the operator included, with no administrator left to approve anyone.
    /// </summary>
    [Fact]
    public async Task Accounts_that_predate_approval_are_activated_by_the_migration()
    {
        Guid userId;

        await using (var before = CreateContext())
        {
            // Migrate to the revision before Status existed, then insert through raw SQL:
            // the entity has a Status property now, so EF could not write a row shaped the
            // way the old schema actually stored one.
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration(before));

            userId = Guid.CreateVersion7();

            await before.Database.ExecuteSqlAsync(
                $"""
                 insert into Users
                     (Id, GoogleSubjectId, Email, DisplayName, CreatedAtUtc, LastSeenAtUtc, IsAdmin, SecurityStamp)
                 values
                     ({userId}, 'legacy-subject', 'legacy@example.com', 'Legacy', '2026-01-01T00:00:00.0000000Z',
                      '2026-01-01T00:00:00.0000000Z', 1, 'stamp')
                 """);
        }

        await using (var upgrade = CreateContext())
        {
            await upgrade.Database.MigrateAsync();
        }

        await using var verify = CreateContext();

        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == userId);

        Assert.Equal(UserStatus.Active, user.Status);
    }

    /// <summary>
    /// The column default is the safety net for any insert that bypasses EF. It must be a
    /// closed status, so a row written by hand is not silently admitted.
    /// </summary>
    [Fact]
    public async Task The_column_default_is_pending()
    {
        await using (var migrate = CreateContext())
        {
            await migrate.Database.MigrateAsync();
        }

        await using var context = CreateContext();

        var userId = Guid.CreateVersion7();

        await context.Database.ExecuteSqlAsync(
            $"""
             insert into Users
                 (Id, GoogleSubjectId, Email, DisplayName, CreatedAtUtc, LastSeenAtUtc, IsAdmin, SecurityStamp)
             values
                 ({userId}, 'default-subject', 'default@example.com', 'Default', '2026-01-01T00:00:00.0000000Z',
                  '2026-01-01T00:00:00.0000000Z', 0, 'stamp')
             """);

        var user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);

        Assert.Equal(UserStatus.Pending, user.Status);
    }

    /// <summary>
    /// Stored as the enum name, so reordering the enum cannot silently reclassify existing
    /// accounts and the column is readable with sqlite3.
    /// </summary>
    [Fact]
    public async Task Status_is_stored_as_a_readable_string()
    {
        await using (var migrate = CreateContext())
        {
            await migrate.Database.MigrateAsync();
        }

        Guid userId;

        await using (var write = CreateContext())
        {
            var user = new User
            {
                GoogleSubjectId = $"subject-{Guid.NewGuid():N}",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                LastSeenAtUtc = DateTimeOffset.UtcNow,
                Status = UserStatus.Disabled,
            };

            write.Users.Add(user);
            await write.SaveChangesAsync();

            userId = user.Id;
        }

        await using var verify = CreateContext();

        var stored = await verify.Database
            .SqlQuery<string>($"select Status as Value from Users where Id = {userId}")
            .SingleAsync();

        Assert.Equal(nameof(UserStatus.Disabled), stored);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The migration immediately before the one under test, looked up rather than hardcoded.
    /// A hardcoded id would still pass after a later migration was inserted in between, while
    /// no longer testing the upgrade step it names.
    /// </summary>
    private static string PreviousMigration(JukeBoxDbContext context)
    {
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToArray();

        var index = Array.FindIndex(
            migrations,
            id => id.EndsWith(MigrationUnderTest, StringComparison.Ordinal));

        Assert.True(index > 0, $"No migration named {MigrationUnderTest} with a predecessor was found.");

        return migrations[index - 1];
    }

    private JukeBoxDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<JukeBoxDbContext>();

        builder.UseJukeBoxSqlite(_databaseFile);

        return new JukeBoxDbContext(builder.Options);
    }
}
