namespace TheJukeBox.Infrastructure.Security;

/// <summary>
/// The response headers that make the browser enforce this application's assumptions.
/// </summary>
/// <remarks>
/// <para>
/// The application already avoids injection by construction -- every value that reaches the DOM is
/// set with <c>textContent</c>, and cover art is served with a sniffed MIME type and
/// <c>nosniff</c>. These headers are the second line: they turn "we do not do that" into "the
/// browser will not allow it", which is what survives a future change nobody thought about.
/// </para>
/// <para>
/// A strict policy is affordable here only because of how the frontend is built. There is exactly
/// one script, loaded from this origin as a module, and no inline handlers anywhere -- so
/// <c>script-src 'self'</c> costs nothing and blocks the entire class. That is the clause worth
/// having; the rest is tidying.
/// </para>
/// </remarks>
public static class SecurityHeaders
{
    /// <summary>
    /// The content security policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>img-src</c> admits Google's avatar hosts, because the account menu shows the picture from
    /// the user's Google profile. If Google ever serves those from elsewhere the image simply fails
    /// and the menu falls back to the initial, which it already does for an expired URL.
    /// </para>
    /// <para>
    /// <c>style-src</c> allows inline styles: the windowed list positions its rows by assigning to
    /// <c>element.style</c>. Style injection is a far narrower problem than script injection, and
    /// giving that up would mean giving up virtualisation.
    /// </para>
    /// <para>
    /// <c>frame-ancestors 'none'</c> is the clickjacking control. This application has one-click
    /// destructive buttons -- delete a playlist, disable an account -- and none of them should ever
    /// be reachable through someone else's invisible iframe.
    /// </para>
    /// </remarks>
    private const string ContentSecurityPolicy =
        "default-src 'self'; "
        + "script-src 'self'; "
        + "style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: https://*.googleusercontent.com; "
        + "media-src 'self'; "
        + "connect-src 'self'; "
        + "font-src 'self'; "
        + "object-src 'none'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'";

    /// <summary>
    /// Adds the headers to every response.
    /// </summary>
    /// <remarks>
    /// Registered early, before anything that can short-circuit the pipeline, so an error page and
    /// a 401 carry the same policy as a successful response. A header set only on the happy path
    /// protects only the happy path.
    /// </remarks>
    public static IApplicationBuilder UseJukeBoxSecurityHeaders(this IApplicationBuilder app, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            headers.XContentTypeOptions = "nosniff";

            // Referrers would otherwise leak the path -- which for this application names an album
            // or an artist -- to any site the user follows a link to.
            headers["Referrer-Policy"] = "no-referrer";

            // The predecessor of frame-ancestors, for browsers that still honour only this.
            headers["X-Frame-Options"] = "DENY";

            // The API reference is the one page that loads a script from a CDN, and it exists only
            // in Development. Exempting it beats weakening the policy for everything else.
            var isApiReference = isDevelopment
                                 && context.Request.Path.StartsWithSegments("/scalar", StringComparison.Ordinal);

            if (!isApiReference)
            {
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
            }

            await next();
        });
    }
}
