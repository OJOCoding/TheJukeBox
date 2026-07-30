using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using TheJukeBox.Configuration;
using TheJukeBox.Diagnostics;
using TheJukeBox.Errors;
using TheJukeBox.Features.Auth;
using TheJukeBox.Features.Library;
using TheJukeBox.Features.Streaming;
using TheJukeBox.Infrastructure.Drive;
using TheJukeBox.Infrastructure.Metadata;
using TheJukeBox.Infrastructure.Persistence;
using TheJukeBox.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// --------------------------------------------------------------------------
// Configuration
// --------------------------------------------------------------------------
// ValidateOnStart moves "you forgot to configure this" from a 500 on the first
// request that happens to need Drive, to a legible message at process start.

builder.Services.AddOptions<GoogleOAuthOptions>()
    .BindConfiguration(GoogleOAuthOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(
        options => !options.ClientId.Contains("YOUR_CLIENT_ID", StringComparison.OrdinalIgnoreCase),
        "ClientId is still a placeholder value.")
    .ValidateOnStart();

builder.Services.AddOptions<AppOptions>()
    .BindConfiguration(AppOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<StorageOptions>()
    .BindConfiguration(StorageOptions.SectionName)
    .ValidateOnStart();

// Resolved eagerly and registered, so nothing downstream is tempted to reach for
// Directory.GetCurrentDirectory() again.
var storageOptions = builder.Configuration
                         .GetSection(StorageOptions.SectionName)
                         .Get<StorageOptions>()
                     ?? new StorageOptions();

JukeBoxPaths paths;

try
{
    paths = new JukeBoxPaths { DataPath = DataPathResolver.Resolve(storageOptions) };
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    StartupValidation.PrintAndExit(e);
    return;
}

builder.Services.AddSingleton(paths);

// --------------------------------------------------------------------------
// Persistence and key management
// --------------------------------------------------------------------------

builder.Services.AddDbContext<JukeBoxDbContext>(options =>
    options.UseJukeBoxSqlite(paths.DatabaseFile));

builder.Services.AddHostedService<DbMigratorHostedService>();

// SetApplicationName is load-bearing rather than cosmetic. Data Protection derives its
// purpose chain from the application name, so leaving it to the default (which is
// content-root based) means moving or renaming the deployment silently produces a
// different key and every stored refresh token becomes undecryptable.
Directory.CreateDirectory(paths.KeysPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysPath))
    .SetApplicationName("TheJukeBox");

builder.Services.AddSingleton<ITokenProtector, DataProtectionTokenProtector>();

// --------------------------------------------------------------------------
// Services
// --------------------------------------------------------------------------

// No AddControllers: every endpoint is a minimal API now that the last controller is
// gone. Leaving MVC registered would add model binding, filters, and an application
// model this app does not use.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IDriveTokenStore, DriveTokenStore>();
builder.Services.AddScoped<IDriveClientFactory, DriveClientFactory>();
builder.Services.AddScoped<IDriveLibraryClient, DriveLibraryClient>();

// The library cache and its background refresh. SyncDispatcher is a singleton because the
// pending-set and the channel must be shared across requests; the worker resolves the
// scoped synchroniser per job.
builder.Services.AddScoped<LibrarySynchroniser>();

// Tag reading. Behind an interface so the library choice stays swappable and the
// extraction pipeline is testable without real audio files.
builder.Services.AddSingleton<ITagReader, AtlTagReader>();
builder.Services.AddScoped<MetadataEnricher>();
builder.Services.AddSingleton<SyncDispatcher>();
builder.Services.AddSingleton<ISyncDispatcher>(provider => provider.GetRequiredService<SyncDispatcher>());
builder.Services.AddHostedService<SyncWorker>();

// Named client for the Drive media endpoint. Registered so the handler and its
// connection pool are shared; a new HttpClient per stream would exhaust sockets.
// No Timeout: the default 100 seconds would abort a long file mid-playback, and the
// request's own cancellation token is the correct bound here.
builder.Services.AddHttpClient(StreamingEndpoints.HttpClientName, client =>
    client.Timeout = Timeout.InfiniteTimeSpan);

// --------------------------------------------------------------------------
// Authentication and authorization
// --------------------------------------------------------------------------
// Bound directly rather than through IOptions because the auth handlers need these
// values while the container is still being built. ValidateOnStart still applies.

var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>()
                 ?? new AppOptions();

var googleOptions = builder.Configuration.GetSection(GoogleOAuthOptions.SectionName).Get<GoogleOAuthOptions>()
                    ?? new GoogleOAuthOptions();

builder.Services.AddJukeBoxAuthentication(appOptions, googleOptions);

// Everything is authenticated unless it opts out. A fallback policy is the safe
// default: forgetting an attribute leaves an endpoint closed rather than open, which
// is the opposite of the previous design where nothing was protected at all.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// One exception handler, one response shape. Replaces the per-action
// catch-and-return-BadRequest(ex.Message) blocks that used to be in every action.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ProblemDetailsExceptionHandler.Enrich);

builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

builder.Services.AddOpenApi();

// No CORS policy at all. The frontend is served from this same origin, so
// cross-origin access is never needed -- and the previous AllowAnyOrigin policy,
// combined with a Drive token held server-side, meant any site the user visited while
// this app was running could read their entire library from localhost.

var app = builder.Build();

// --------------------------------------------------------------------------
// Pipeline
// --------------------------------------------------------------------------

// First, before anything reads the scheme or host. Behind a TLS-terminating proxy
// Kestrel otherwise sees plain HTTP, Request.IsHttps is false, and every Secure cookie
// is silently dropped -- sign-in appears to work and then does nothing.
app.UseForwardedHeaders(AuthenticationSetup.CreateForwardedHeadersOptions(appOptions));

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// UseDefaultFiles rewrites "/" to "/index.html" before UseStaticFiles serves it, so
// the order here matters.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapSessionEndpoint();
app.MapLibraryEndpoints();
app.MapArtEndpoints();
app.MapStreamingEndpoints();

app.MapGet("/api/health", () => TypedResults.Ok(new { status = "ok" })).AllowAnonymous();

// An unmatched /api path must never fall through to the SPA fallback below. A fetch
// that expects JSON and receives an HTML document fails in a far more confusing way
// than a 404 does. Authorization still applies, so an anonymous caller gets 401 here
// too and cannot probe which routes exist.
app.Map("/api/{**rest}", () => Results.NotFound());

// Deep links. The client router owns /library, /albums and the rest, so any
// non-API path that is not a real file serves the shell and lets the client resolve it.
// Without this, opening or refreshing a deep link 404s.
app.MapFallbackToFile("index.html").AllowAnonymous();

// --------------------------------------------------------------------------
// Run
// --------------------------------------------------------------------------

try
{
    LogStartupFacts(app);
    app.Run();
}
catch (Exception e) when (StartupValidation.IsConfigurationFailure(e))
{
    StartupValidation.PrintAndExit(e);
}

return;

// Emitted before Run so the two facts most often needed to debug a deployment --
// where state is written, and the exact redirect URIs Google must be told about --
// are the first thing in the log, and copy-pasteable.
static void LogStartupFacts(WebApplication application)
{
    // Touching every validated options type here, before app.Run(), means a
    // misconfiguration is thrown from this call and caught below. Leaving it to the
    // host's own StartupValidator works too, but the host logs "Hosting failed to
    // start" with a full stack trace first, burying the readable explanation.
    _ = application.Services.GetRequiredService<IOptions<GoogleOAuthOptions>>().Value;

    var appOptions = application.Services.GetRequiredService<IOptions<AppOptions>>().Value;
    var dataPath = application.Services.GetRequiredService<JukeBoxPaths>().DataPath;

    Log.DataPathResolved(application.Logger, dataPath);
    Log.RedirectUris(application.Logger, appOptions.SignInRedirectUri, appOptions.DriveRedirectUri);
}
