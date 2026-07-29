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
}
