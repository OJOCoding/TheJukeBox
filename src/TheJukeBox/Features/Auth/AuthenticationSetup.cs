using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.HttpOverrides;
using TheJukeBox.Configuration;
using TheJukeBox.Infrastructure.Drive;

namespace TheJukeBox.Features.Auth;

/// <summary>
/// Registers cookie sessions and Google sign-in.
/// </summary>
public static class AuthenticationSetup
{
    /// <summary>Session cookie name when the instance is served over HTTPS.</summary>
    private const string SecureCookieName = "__Host-jukebox";

    /// <summary>Fallback name for a plain-HTTP instance, where __Host- cannot apply.</summary>
    private const string InsecureCookieName = "jukebox.session";

    public static IServiceCollection AddJukeBoxAuthentication(
        this IServiceCollection services,
        AppOptions appOptions,
        GoogleOAuthOptions googleOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(appOptions);
        ArgumentNullException.ThrowIfNull(googleOptions);

        var overHttps = appOptions.PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        services.AddScoped<GoogleSignInService>();

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => ConfigureCookie(options, overHttps))
            .AddGoogle(options => ConfigureGoogle(options, appOptions, googleOptions));

        services.AddAuthorization();

        // Anti-forgery for state-changing requests. The header name is what the
        // frontend will echo from the XSRF-TOKEN cookie.
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-XSRF-TOKEN";
            options.Cookie.Name = overHttps ? "__Host-jukebox-xsrf" : "jukebox.xsrf";
            options.Cookie.SecurePolicy = overHttps
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
        });

        return services;
    }

    /// <summary>
    /// Trusts a reverse proxy's forwarded headers.
    /// </summary>
    /// <remarks>
    /// Must be the first middleware. Without it, Kestrel behind a TLS-terminating proxy
    /// sees <c>http://container:8080</c>, which makes <c>Request.IsHttps</c> false and
    /// silently suppresses every <c>Secure</c> cookie, so login appears to succeed and
    /// then does nothing.
    /// </remarks>
    public static ForwardedHeadersOptions CreateForwardedHeadersOptions(AppOptions appOptions)
    {
        ArgumentNullException.ThrowIfNull(appOptions);

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                               | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                               | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
        };

        if (appOptions.TrustForwardedHeadersFromAnyProxy)
        {
            // Only correct when the proxy is the sole ingress. Otherwise a client can
            // assert its own scheme and host, which is why this is opt-in rather than
            // the default.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        }

        return options;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions options, bool overHttps)
    {
        // The __Host- prefix is the strongest binding a cookie can have: the browser
        // enforces Secure, no Domain attribute, and Path=/. It is also impossible to
        // satisfy over plain HTTP, so a LAN instance without TLS would have login
        // silently fail. Detect that and step down rather than ship something broken.
        options.Cookie.Name = overHttps ? SecureCookieName : InsecureCookieName;
        options.Cookie.SecurePolicy = overHttps
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;

        options.Cookie.HttpOnly = true;

        // Lax, not Strict: the OAuth callback is a top-level navigation initiated by
        // Google, and Strict would withhold the cookie on arrival, breaking sign-in.
        options.Cookie.SameSite = SameSiteMode.Lax;

        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;

        options.LoginPath = "/api/auth/login";
        options.LogoutPath = "/api/auth/logout";
        options.AccessDeniedPath = "/";

        // An API call must be told "you are not signed in", not handed a redirect to a
        // login page. A 302 to HTML is what makes a fetch() look like it succeeded and
        // return a login document where JSON was expected, and it is the single thing
        // the frontend's session handling depends on most.
        options.Events.OnRedirectToLogin = context => Challenge(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => Challenge(context, StatusCodes.Status403Forbidden);

        // Revalidate against the database on every request. A fourteen-day sliding cookie
        // that is never re-checked means disabling an account has no effect for a fortnight,
        // and approving one has no effect until the user happens to sign in again.
        options.Events.OnValidatePrincipal = UserSessionValidator.ValidateAsync;
    }

    private static Task Challenge(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (WantsHtml(context.Request))
        {
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        }

        context.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }

    private static bool WantsHtml(HttpRequest request) =>
        request.Headers.Accept.Any(value =>
            value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);

    private static void ConfigureGoogle(
        GoogleOptions options,
        AppOptions appOptions,
        GoogleOAuthOptions googleOptions)
    {
        options.ClientId = googleOptions.ClientId;
        options.ClientSecret = googleOptions.ClientSecret;

        // Must match the path component of AppOptions.SignInRedirectUri, which is what
        // the self-hoster registers in Cloud Console.
        options.CallbackPath = "/api/auth/callback";

        // Identity and Drive are requested together in one consent screen. A separate
        // "now also grant Drive" step was considered and rejected: this application is
        // useless without Drive, so splitting it only adds a step everyone must
        // complete anyway.
        options.Scope.Add("email");
        options.Scope.Add("profile");

        foreach (var scope in DriveClientFactory.Scopes)
        {
            options.Scope.Add(scope);
        }

        // access_type=offline is what asks for a refresh token at all.
        options.AccessType = "offline";

        // prompt=consent is what makes Google issue a *new* refresh token on a repeat
        // authorisation. Without it, a user who has already granted access comes back
        // with an access token only, so the "reconnect my Drive" path after a revoked or
        // expired grant would appear to succeed and change nothing.
        options.AdditionalAuthorizationParameters["prompt"] = "consent";

        // SaveTokens stores tokens in the auth cookie. Deliberately off: a refresh
        // token is long-lived unattended access to someone's Drive and belongs in an
        // encrypted database column, not in a value round-tripping through a browser.
        options.SaveTokens = false;

        options.ClaimActions.MapJsonKey("urn:google:picture", "picture", "url");

        options.Events.OnCreatingTicket = async context =>
        {
            var signIn = context.HttpContext.RequestServices.GetRequiredService<GoogleSignInService>();

            await signIn.CompleteAsync(context);
        };

        options.Events.OnRedirectToAuthorizationEndpoint = context =>
        {
            // Pin redirect_uri to the configured public base URL rather than whatever
            // Kestrel inferred from the request. Behind a reverse proxy the inferred
            // value does not match what was registered, and Google answers with an
            // opaque redirect_uri_mismatch that says nothing about why.
            var uri = QueryStringRewriter.Replace(
                context.RedirectUri,
                "redirect_uri",
                appOptions.SignInRedirectUri);

            context.Response.Redirect(uri);

            return Task.CompletedTask;
        };

        options.Events.OnRemoteFailure = context =>
        {
            // The user declining consent is not an error worth a stack trace. Send them
            // back to the app with a fixed marker; never echo Google's message, which is
            // remote input.
            context.HandleResponse();
            context.Response.Redirect("/?authError=denied");

            return Task.CompletedTask;
        };

        options.Events.OnTicketReceived = context =>
        {
            // Deep links survive the OAuth round trip: /api/auth/login records where the
            // user was going, and the ticket handler sends them there.
            context.ReturnUri = SafeReturnUrl.Sanitise(context.Properties?.RedirectUri);

            return Task.CompletedTask;
        };
    }
}
