using System.ComponentModel.DataAnnotations;

namespace TheJukeBox.Configuration;

/// <summary>
/// Deployment-level settings for this instance.
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    /// The absolute base URL browsers reach this instance on, with no trailing
    /// slash. Every OAuth redirect URI is derived from this.
    /// </summary>
    /// <remarks>
    /// This exists because the redirect URI a Google handler generates otherwise
    /// comes from <c>Request.Scheme</c> + <c>Request.Host</c>. Behind a
    /// TLS-terminating reverse proxy Kestrel sees <c>http://jukebox:8080</c> and
    /// produces a redirect URI that does not match what the self-hoster
    /// registered, yielding Google's opaque <c>redirect_uri_mismatch</c>. Pinning
    /// the value here makes the URI exactly the string they pasted into Cloud
    /// Console regardless of proxy configuration.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [Url(ErrorMessage = "PublicBaseUrl must be an absolute URL, e.g. https://music.example.com")]
    public string PublicBaseUrl { get; init; } = "";

    /// <summary>
    /// Accept X-Forwarded-* from any peer. Only safe when the reverse proxy is the
    /// sole ingress; otherwise a client can spoof its own scheme and host.
    /// </summary>
    public bool TrustForwardedHeadersFromAnyProxy { get; init; }

    /// <summary>The OAuth redirect URI for sign-in, derived from PublicBaseUrl.</summary>
    public string SignInRedirectUri => $"{PublicBaseUrl.TrimEnd('/')}/api/auth/callback";

    /// <summary>The OAuth redirect URI for the Drive grant, derived from PublicBaseUrl.</summary>
    public string DriveRedirectUri => $"{PublicBaseUrl.TrimEnd('/')}/api/auth/drive/callback";
}
