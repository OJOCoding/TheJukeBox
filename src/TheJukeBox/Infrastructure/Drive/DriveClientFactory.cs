using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using TheJukeBox.Configuration;
using TheJukeBox.Diagnostics;
using TheJukeBox.Errors;
using TheJukeBox.Infrastructure.Security;

namespace TheJukeBox.Infrastructure.Drive;

/// <summary>
/// Builds per-user Drive clients from stored refresh tokens.
/// </summary>
/// <remarks>
/// Registered scoped, so one credential is built per request and an access token
/// obtained by one call is reused by later calls in the same request. Caching access
/// tokens across requests is a later concern; getting the ownership boundary right
/// matters more than saving a refresh round trip.
/// </remarks>
internal sealed class DriveClientFactory(
    IOptions<GoogleOAuthOptions> googleOptions,
    IDriveTokenStore tokenStore,
    ILogger<DriveClientFactory> logger) : IDriveClientFactory
{
    private const string ApplicationName = "TheJukeBox";

    /// <summary>
    /// Read-only Drive access. Least privilege: this application never needs to
    /// create, modify, or delete anything in someone's Drive.
    /// </summary>
    public static readonly string[] Scopes = [DriveService.Scope.DriveReadonly];

    public async Task<DriveService> CreateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var refreshToken = await tokenStore.GetRefreshTokenAsync(userId, cancellationToken)
                           ?? throw new DriveConsentRequiredException();

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = googleOptions.Value.ClientId,
                ClientSecret = googleOptions.Value.ClientSecret,
            },
            Scopes = Scopes,

            // No DataStore. The library's FileDataStore is what produced the original
            // single-token-on-disk design; persistence here is the encrypted database
            // column, and the flow is only used for the refresh exchange.
        });

        // No access token, so the credential treats itself as stale and exchanges the
        // refresh token on first use.
        var credential = new UserCredential(flow, userId.ToString(), new TokenResponse
        {
            RefreshToken = refreshToken,
        });

        try
        {
            if (!await credential.RefreshTokenAsync(cancellationToken))
            {
                await RetireAsync(userId, "refresh_failed", cancellationToken);
                throw new DriveConsentRequiredException();
            }
        }
        catch (TokenResponseException e)
        {
            // invalid_grant is terminal: the user revoked access, changed their
            // password, or the refresh token expired because the OAuth consent screen
            // is still in Testing status, where Google expires them after 7 days.
            // Retrying cannot help, so retire the grant and ask for consent again.
            var error = e.Error?.Error;

            await RetireAsync(userId, error ?? "token_response_error", cancellationToken);

            throw new DriveConsentRequiredException(
                "Google rejected the stored authorisation. Please reconnect your Drive.");
        }

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName,
        });
    }

    private async Task RetireAsync(Guid userId, string errorCode, CancellationToken cancellationToken)
    {
        Log.DriveGrantRetired(logger, userId, errorCode);

        await tokenStore.MarkRevokedAsync(userId, errorCode, cancellationToken);
    }
}
