namespace TheJukeBox.Errors;

/// <summary>
/// Stable machine-readable <c>type</c> values for RFC 9457 problem responses.
/// </summary>
/// <remarks>
/// The frontend switches on these, so they are part of the API contract and must
/// not be renamed casually. <c>title</c> and <c>detail</c> are human-facing and
/// free to change.
/// </remarks>
public static class ProblemTypes
{
    private const string Prefix = "urn:jukebox:";

    public const string Internal = Prefix + "internal";
    public const string Validation = Prefix + "validation";
    public const string NotFound = Prefix + "not-found";
    public const string Unauthenticated = Prefix + "unauthenticated";

    /// <summary>
    /// The caller is signed in but this instance holds no usable Drive grant for
    /// them, so the UI should prompt to (re)connect rather than show an error.
    /// </summary>
    public const string DriveConsentRequired = Prefix + "drive-consent-required";

    /// <summary>Google Drive refused or failed the upstream request.</summary>
    public const string DriveUnavailable = Prefix + "drive-unavailable";

    /// <summary>A supplied Drive identifier was not of a valid shape.</summary>
    public const string InvalidDriveIdentifier = Prefix + "invalid-drive-identifier";
}
