using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using TheJukeBox.API;
using TheJukeBox.Configuration;
using TheJukeBox.Diagnostics;
using TheJukeBox.Errors;
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

builder.Services.AddControllers();

builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
builder.Services.AddScoped<IGoogleDriveService, GoogleDriveService>();

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

app.MapControllers();

app.MapGet("/player", (IWebHostEnvironment environment) =>
    TypedResults.PhysicalFile(Path.Combine(environment.WebRootPath, "player.html"), "text/html"));

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
