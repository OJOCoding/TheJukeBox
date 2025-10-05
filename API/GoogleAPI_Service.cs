using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.Text.Json;
using System.Web;

namespace TheJukeBox.API
{
    public interface IGoogleAuthService
    {
        string GetAuthorizationUrl();
        Task<DriveService> ExchangeCodeForTokenAsync(string code);
        Task<DriveService> GetValidDriveServiceAsync();
        Task<DriveService> TryLoadExistingTokenAsync();
        Task<bool> IsTokenValidAsync();
        bool HasStoredToken();
        string GetConfigInfo();
    }

    public class GoogleAPI_Service : IGoogleAuthService
    {
        private readonly string[] Scopes = { DriveService.Scope.DriveReadonly };
        private readonly string ApplicationName = "Jukebox App";
        private readonly string CredentialsPath;
        private readonly string TokenDirectory;
        private readonly string RedirectUri = "https://localhost:7077/api/auth/callback";

        private string _clientId;
        private string _clientSecret;

        public GoogleAPI_Service()
        {
            CredentialsPath = Path.Combine(Directory.GetCurrentDirectory(), "AUTH", "credentials.json");
            TokenDirectory = Path.Combine(Directory.GetCurrentDirectory(), "AUTH");

            if (!Directory.Exists(TokenDirectory))
                Directory.CreateDirectory(TokenDirectory);

            LoadCredentials();
        }

        private void LoadCredentials()
        {
            if (!File.Exists(CredentialsPath))
                throw new FileNotFoundException($"Credentials file not found at: {CredentialsPath}");

            var json = File.ReadAllText(CredentialsPath);
            var jsonDoc = JsonDocument.Parse(json);

            JsonElement credentials;
            if (jsonDoc.RootElement.TryGetProperty("web", out credentials))
            {
                _clientId = credentials.GetProperty("client_id").GetString();
                _clientSecret = credentials.GetProperty("client_secret").GetString();
            }
            else if (jsonDoc.RootElement.TryGetProperty("installed", out credentials))
            {
                _clientId = credentials.GetProperty("client_id").GetString();
                _clientSecret = credentials.GetProperty("client_secret").GetString();
            }
            else
            {
                throw new InvalidOperationException("Invalid credentials file format");
            }
        }

        public async Task<bool> IsTokenValidAsync()
        {
            try
            {
                var driveService = await TryLoadExistingTokenAsync();
                if (driveService == null) return false;

                var request = driveService.Files.List();
                request.PageSize = 1;
                await request.ExecuteAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<DriveService> GetValidDriveServiceAsync()
        {
            var driveService = await TryLoadExistingTokenAsync();
            if (driveService != null)
            {
                try
                {
                    var request = driveService.Files.List();
                    request.PageSize = 1;
                    await request.ExecuteAsync();
                    return driveService;
                }
                catch
                {
                    // Token is invalid
                }
            }

            throw new InvalidOperationException("Authentication required. Please call /api/auth/setup first.");
        }

        public async Task<DriveService> TryLoadExistingTokenAsync()
        {
            try
            {
                var tokenFilePath = Path.Combine(TokenDirectory, "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user");

                if (!File.Exists(tokenFilePath))
                    return null;

                var clientSecrets = new ClientSecrets
                {
                    ClientId = _clientId,
                    ClientSecret = _clientSecret
                };

                var dataStore = new FileDataStore(TokenDirectory, true);

                var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = clientSecrets,
                    Scopes = Scopes,
                    DataStore = dataStore
                });

                var token = await flow.LoadTokenAsync("user", CancellationToken.None);

                if (token == null)
                    return null;

                if (token.IsExpired(flow.Clock))
                {
                    var credential = new UserCredential(flow, "user", token);
                    if (await credential.RefreshTokenAsync(CancellationToken.None))
                    {
                        return new DriveService(new BaseClientService.Initializer()
                        {
                            HttpClientInitializer = credential,
                            ApplicationName = ApplicationName,
                        });
                    }
                    else
                    {
                        return null;
                    }
                }

                var validCredential = new UserCredential(flow, "user", token);
                return new DriveService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = validCredential,
                    ApplicationName = ApplicationName,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading token: {ex.Message}");
                return null;
            }
        }

        public bool HasStoredToken()
        {
            try
            {
                var tokenFilePath = Path.Combine(TokenDirectory, "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user");
                return File.Exists(tokenFilePath);
            }
            catch
            {
                return false;
            }
        }

        public string GetAuthorizationUrl()
        {
            var authorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth?" +
                $"client_id={HttpUtility.UrlEncode(_clientId)}&" +
                $"response_type=code&" +
                $"scope={HttpUtility.UrlEncode(string.Join(" ", Scopes))}&" +
                $"redirect_uri={HttpUtility.UrlEncode(RedirectUri)}&" +
                $"access_type=offline&" +
                $"prompt=consent";

            return authorizationUrl;
        }

        public async Task<DriveService> ExchangeCodeForTokenAsync(string code)
        {
            var clientSecrets = new ClientSecrets
            {
                ClientId = _clientId,
                ClientSecret = _clientSecret
            };

            var dataStore = new FileDataStore(TokenDirectory, true);

            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = clientSecrets,
                Scopes = Scopes,
                DataStore = dataStore
            });

            var tokenResponse = await flow.ExchangeCodeForTokenAsync(
                "user",
                code,
                RedirectUri,
                CancellationToken.None);

            var credential = new UserCredential(flow, "user", tokenResponse);

            return new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });
        }

        public string GetConfigInfo()
        {
            return $"Client ID: {_clientId}\n" +
                   $"Redirect URI: {RedirectUri}\n" +
                   $"Credentials file: {CredentialsPath}\n" +
                   $"Token directory: {TokenDirectory}";
        }
    }
}