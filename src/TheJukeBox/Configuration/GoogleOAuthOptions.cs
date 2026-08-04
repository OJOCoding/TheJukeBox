using System.ComponentModel.DataAnnotations;

namespace TheJukeBox.Configuration;

/// <summary>
/// Google OAuth client credentials.
/// </summary>
/// <remarks>
/// This replaces <c>CORE/GoogleAuthConfig.cs</c>, which declared the same three
/// fields in snake_case and was never registered or read by anything. Credentials
/// used to be loaded by reading <c>AUTH/credentials.json</c> from disk inside a
/// service constructor, which threw <see cref="FileNotFoundException"/> at DI
/// resolution time and surfaced as an opaque 500 on the first request that
/// happened to need Drive. Binding here with ValidateOnStart moves that failure
/// to process start, where it can be explained.
/// <para>
/// <c>project_id</c> from the old credentials file is deliberately dropped: it was
/// never read and plays no part in the OAuth flow.
/// </para>
/// </remarks>
public sealed class GoogleOAuthOptions
{
    public const string SectionName = "Authentication:Google";

    [Required(AllowEmptyStrings = false, ErrorMessage = "A Google OAuth client id is required.")]
    [MinLength(12, ErrorMessage = "This does not look like a Google client id (too short).")]
    public string ClientId { get; init; } = "";

    [Required(AllowEmptyStrings = false, ErrorMessage = "A Google OAuth client secret is required.")]
    [MinLength(12, ErrorMessage = "This does not look like a Google client secret (too short).")]
    public string ClientSecret { get; init; } = "";

    /// <summary>
    /// Whether these credentials are obviously not real ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Startup validation can only prove a value is present, not that Google recognises it.
    /// Without this check the first thing a misconfigured self-hoster sees is Google's
    /// "Access blocked: Authorization Error / Error 401: invalid_client" page, which says
    /// nothing about which end is at fault, offers no way back, and is reached only after
    /// clicking sign-in. Detecting the obvious cases lets the app say so itself, before the
    /// user leaves it.
    /// </para>
    /// <para>
    /// Deliberately conservative. It looks for markers that cannot occur in a real client id
    /// rather than trying to validate the format, because a false positive here would tell
    /// someone their working credentials are broken.
    /// </para>
    /// </remarks>
    public bool LooksLikePlaceholder =>
        string.IsNullOrWhiteSpace(ClientId)
        || !ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase)
        || PlaceholderMarkers.Any(marker => ClientId.Contains(marker, StringComparison.OrdinalIgnoreCase))

        // A real client id begins with the numeric Cloud project number, never all zeros.
        || ClientId.StartsWith("000000000000-", StringComparison.Ordinal);

    /// <summary>
    /// A redacted fragment of the client id, for diagnostics.
    /// </summary>
    /// <remarks>
    /// A client id is not a secret -- it is sent to Google in a URL the user can read -- but it
    /// is still an identifier, so only the leading project number and the tail are shown. That
    /// is enough to answer the question this exists for: "is the app reading the value I set, or
    /// a different one?" Someone who has set credentials in the wrong place sees a number that is
    /// not theirs, which names the problem far faster than "not configured" does.
    /// </remarks>
    public string ClientIdHint =>
        string.IsNullOrWhiteSpace(ClientId)
            ? "empty"
            : ClientId.Length <= 16
                ? $"{ClientId.Length} characters, not a Google client id"
                : $"{ClientId[..Math.Min(13, ClientId.Length)]}…{ClientId[^12..]}";

    private static readonly string[] PlaceholderMarkers =
    [
        "placeholder",
        "your-client",
        "your_client",
        "yourclientid",
        "changeme",
        "example",
        "xxxx",
    ];
}
