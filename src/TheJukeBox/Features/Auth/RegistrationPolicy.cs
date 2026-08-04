using TheJukeBox.Configuration;
using TheJukeBox.Domain;

namespace TheJukeBox.Features.Auth;

/// <summary>
/// Decides what happens to an account the first time it signs in.
/// </summary>
public static class RegistrationPolicy
{
    /// <summary>
    /// The status a brand-new account starts in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operator is always activated, in every mode. Nobody can approve the first account
    /// but itself, so making it pending would lock the instance permanently.
    /// </para>
    /// <para>
    /// Everyone else depends on the mode. Note that a rejected sign-in still creates a row:
    /// the account has to exist for an administrator to be able to approve it, and recording
    /// the attempt is also how the operator finds out someone tried.
    /// </para>
    /// </remarks>
    public static UserStatus StatusForNewAccount(bool isFirstAccount, RegistrationMode mode) =>
        (isFirstAccount, mode) switch
        {
            (true, _) => UserStatus.Active,
            (false, RegistrationMode.Open) => UserStatus.Active,

            // SingleUser records the attempt but never activates it, and unlike AdminApproval
            // the approve endpoint refuses it outright.
            (false, RegistrationMode.SingleUser) => UserStatus.Pending,

            // AdminApproval, and anything a future version of the enum adds. Defaulting an
            // unrecognised mode to Pending means a configuration typo cannot fail open.
            _ => UserStatus.Pending,
        };

    /// <summary>
    /// Whether an administrator is allowed to approve this account under the current mode.
    /// </summary>
    public static bool CanApprove(bool isAdminAccount, RegistrationMode mode) =>
        mode != RegistrationMode.SingleUser || isAdminAccount;
}
