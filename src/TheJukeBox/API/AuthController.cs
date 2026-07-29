using Microsoft.AspNetCore.Mvc;
using TheJukeBox.Diagnostics;
using TheJukeBox.Errors;

namespace TheJukeBox.API;

/// <summary>
/// The single-tenant Google OAuth flow. Superseded in A4.
/// </summary>
/// <remarks>
/// Three things were removed outright rather than carried forward:
/// <list type="bullet">
/// <item>
/// The callback built HTML by concatenating the raw <c>code</c> query parameter and
/// <c>ex.Message</c> into a response body. That is reflected XSS, it echoed an OAuth
/// authorization code back to whoever supplied it, and it shipped a second visual
/// design language (Arial, rounded corners, green buttons) inside a product whose
/// stylesheet asserts the exact opposite. It now redirects and renders nothing, so
/// there is no injection surface left to get wrong.
/// </item>
/// <item>
/// A <c>debug</c> endpoint returned every request header as JSON. That includes
/// <c>Cookie</c> and <c>Authorization</c>, making it a credential-disclosure endpoint
/// reachable by anyone.
/// </item>
/// <item>
/// <c>CleanAuthorizationCode</c> stripped <c>&amp;</c>, <c>$</c>, <c>-</c> and
/// whitespace from authorization codes. Real Google codes contain <c>-</c>, so it
/// would have corrupted every code it touched; nothing called it.
/// </item>
/// </list>
/// </remarks>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IGoogleAuthService authService,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>Where the browser lands after the OAuth round trip.</summary>
    /// <remarks>
    /// A constant, never anything derived from the request. Building this from a
    /// caller-supplied <c>returnUrl</c> without validating it as a same-origin path is
    /// how redirect endpoints become open redirects.
    /// </remarks>
    private const string LandingPath = "/";

    /// <summary>Begins the OAuth flow.</summary>
    [HttpGet("setup")]
    public IActionResult GetSetupInfo() => Ok(new
    {
        success = true,
        message = "Open auth_url to grant this instance read-only access to your Drive.",
        auth_url = authService.GetAuthorizationUrl(),

        // Deliberately no config dump. The previous response included the OAuth client
        // id and absolute filesystem paths, none of which a caller needs.
    });

    /// <summary>Reports whether this instance currently holds a Drive grant.</summary>
    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new
    {
        success = true,

        // A file-existence check. The endpoint this replaces ran a live Files.List
        // probe purely to answer "is the token good?", spending 100 quota units on
        // every poll; a genuine failure surfaces on the next real library call anyway,
        // as a typed drive-consent-required problem.
        connected = authService.HasStoredToken(),
    });

    /// <summary>Google's OAuth redirect target.</summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(error))
        {
            // Google's error slug is logged, not echoed, and is replaced by a fixed
            // token so nothing caller-controlled reaches the response.
            Log.ClientProblem(logger, ProblemTypes.Unauthenticated, "GET", "/api/auth/callback", error);
            return Redirect($"{LandingPath}?authError=denied");
        }

        if (string.IsNullOrEmpty(code))
        {
            return Redirect($"{LandingPath}?authError=missing_code");
        }

        try
        {
            await authService.ExchangeCodeForTokenAsync(code, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.OAuthCallbackFailed(logger, e);
            return Redirect($"{LandingPath}?authError=exchange_failed");
        }

        return Redirect($"{LandingPath}?connected=1");
    }
}
