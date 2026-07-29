using System.Web;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Options;
using TheJukeBox.Configuration;
using TheJukeBox.Diagnostics;
using TheJukeBox.Errors;

namespace TheJukeBox.API;

public interface IGoogleAuthService
{
    string GetAuthorizationUrl();

    Task<DriveService> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken = default);

    Task<DriveService> GetValidDriveServiceAsync(CancellationToken cancellationToken = default);

    Task<DriveService?> TryLoadExistingTokenAsync(CancellationToken cancellationToken = default);

    bool HasStoredToken();
}

/// <summary>
/// Single-tenant Google Drive authentication. Superseded in A4.
/// </summary>
/// <remarks>
/// This still stores exactly one token under the fixed key <c>"user"</c>, which is
/// the root cause of the app having no authorization model at all: with no identity
/// there is nothing to authorize against, so every caller shares the owner's Drive.
/// A4 replaces it wholesale with per-user encrypted tokens. What changed here is only
/// the parts that were independently wrong: credentials come from configuration
/// rather than a JSON file read inside the constructor, the redirect URI is derived
/// from configuration rather than hardcoded to port 7077, and failures are typed
/// rather than stringly.
/// <para>
/// Renamed from <c>GoogleAPI_Service</c>, whose underscore violated CA1707 and
/// clashed stylistically with every other type in the project.
/// </para>
/// </remarks>
public sealed class GoogleAuthService : IGoogleAuthService
{
    private const string TokenKey = "user";
    private const string ApplicationName = "TheJukeBox";

    private static readonly string[] Scopes = [DriveService.Scope.DriveReadonly];

    private readonly ILogger<GoogleAuthService> _logger;
    private readonly string _tokenDirectory;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _redirectUri;

    public GoogleAuthService(
        IOptions<GoogleOAuthOptions> googleOptions,
        IOptions<AppOptions> appOptions,
        JukeBoxPaths paths,
        ILogger<GoogleAuthService> logger)
    {
        _logger = logger;
        _clientId = googleOptions.Value.ClientId;
        _clientSecret = googleOptions.Value.ClientSecret;
        _redirectUri = appOptions.Value.SignInRedirectUri;
        _tokenDirectory = paths.TokensPath;

        Directory.CreateDirectory(_tokenDirectory);
    }

    public string GetAuthorizationUrl() =>
        // NOTE(A4): this hand-rolled URL carries no `state` parameter, so the callback
        // will accept an authorization code from anyone. The framework's Google
        // handler supplies state and PKCE for free, which is why A4 deletes this.
        "https://accounts.google.com/o/oauth2/v2/auth" +
        $"?client_id={HttpUtility.UrlEncode(_clientId)}" +
        "&response_type=code" +
        $"&scope={HttpUtility.UrlEncode(string.Join(' ', Scopes))}" +
        $"&redirect_uri={HttpUtility.UrlEncode(_redirectUri)}" +
        "&access_type=offline" +
        "&prompt=consent";

    public async Task<DriveService> ExchangeCodeForTokenAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var flow = CreateFlow();

        var tokenResponse = await flow.ExchangeCodeForTokenAsync(
            TokenKey, code, _redirectUri, cancellationToken);

        return CreateDriveService(new UserCredential(flow, TokenKey, tokenResponse));
    }

    public async Task<DriveService> GetValidDriveServiceAsync(CancellationToken cancellationToken = default) =>
        await TryLoadExistingTokenAsync(cancellationToken)
        ?? throw new DriveConsentRequiredException();

    public async Task<DriveService?> TryLoadExistingTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var flow = CreateFlow();
            var token = await flow.LoadTokenAsync(TokenKey, cancellationToken);

            if (token is null)
            {
                return null;
            }

            var credential = new UserCredential(flow, TokenKey, token);

            // IsStale supersedes the obsolete IsExpired(IClock): it accounts for the
            // refresh window rather than only the hard expiry instant.
            if (token.IsStale && !await credential.RefreshTokenAsync(cancellationToken))
            {
                return null;
            }

            return CreateDriveService(credential);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A malformed or revoked token must not become a 500. Reporting "no token"
            // lets the caller surface a reconnect prompt instead.
            Log.TokenLoadFailed(_logger, e);
            return null;
        }
    }

    public bool HasStoredToken() =>
        File.Exists(Path.Combine(
            _tokenDirectory,
            $"Google.Apis.Auth.OAuth2.Responses.TokenResponse-{TokenKey}"));

    private GoogleAuthorizationCodeFlow CreateFlow() => new(new GoogleAuthorizationCodeFlow.Initializer
    {
        ClientSecrets = new ClientSecrets { ClientId = _clientId, ClientSecret = _clientSecret },
        Scopes = Scopes,
        DataStore = new FileDataStore(_tokenDirectory, fullPath: true),
    });

    private static DriveService CreateDriveService(UserCredential credential) =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName,
        });
}
