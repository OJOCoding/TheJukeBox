namespace TheJukeBox.Configuration;

/// <summary>How new accounts are handled.</summary>
public enum RegistrationMode
{
    /// <summary>
    /// New accounts land in <see cref="Domain.UserStatus.Pending"/> and an administrator
    /// approves them.
    /// </summary>
    /// <remarks>
    /// The default, and deliberately the restrictive one. Any Google account on the internet
    /// can reach a public sign-in page, so an instance that activated everyone by default
    /// would hand strangers a library and consume the Cloud project's permanent 100-user
    /// unverified-app cap. Defaulting to open would be a footgun aimed at whoever deploys
    /// this first.
    /// </remarks>
    AdminApproval = 0,

    /// <summary>Anyone who signs in is immediately active.</summary>
    /// <remarks>
    /// Reasonable when the instance is already behind other authentication, or genuinely
    /// intended to be public and the operator has accepted what that means.
    /// </remarks>
    Open = 1,

    /// <summary>Only the first account, the administrator, may ever exist.</summary>
    /// <remarks>
    /// For a single-person instance. Later sign-ins are recorded as pending and can never be
    /// approved through the UI, so an exposed instance leaks nothing but a row.
    /// </remarks>
    SingleUser = 2,
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public RegistrationMode Registration { get; init; } = RegistrationMode.AdminApproval;
}
