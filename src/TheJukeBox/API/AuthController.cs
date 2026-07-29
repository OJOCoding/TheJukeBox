using Microsoft.AspNetCore.Mvc;

namespace TheJukeBox.API
{
        [ApiController]
        [Route("api/[controller]")]
        public class AuthController : ControllerBase
        {
            private readonly IGoogleAuthService _authService;

            public AuthController(IGoogleAuthService authService)
            {
                _authService = authService;
            }

        [HttpGet("callback")]
        public async Task<IActionResult> Callback([FromQuery] string code)
        {
            try
            {
                if (string.IsNullOrEmpty(code))
                {
                    return Content(@"
                <html>
                    <head><title>Error</title></head>
                    <body style='font-family: Arial, sans-serif; padding: 20px;'>
                        <h1>Missing Code</h1>
                        <p>No authorization code found in the URL.</p>
                        <p>Please try the authentication process again.</p>
                    </body>
                </html>", "text/html");
                }

                // Exchange code for token
                var driveService = await _authService.ExchangeCodeForTokenAsync(code);

                // Test the connection
                var request = driveService.Files.List();
                request.PageSize = 5;
                request.Fields = "files(id, name)";
                var result = await request.ExecuteAsync();

                return Content(@"
            <!DOCTYPE html>
            <html>
            <head>
                <title>Authentication Successful</title>
                <style>
                    body { font-family: Arial, sans-serif; padding: 40px; text-align: center; background: #f5f5f5; }
                    .container { background: white; padding: 30px; border-radius: 10px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); max-width: 500px; margin: 0 auto; }
                    .success { color: #4CAF50; font-size: 24px; }
                    .button { background: #4CAF50; color: white; padding: 12px 24px; border: none; border-radius: 5px; cursor: pointer; font-size: 16px; margin: 10px; }
                </style>
            </head>
            <body>
                <div class='container'>
                    <div class='success'>Authentication Successful!</div>
                    <p>Your Google Drive connection has been established.</p>
                    <p><strong>Files accessible:</strong> " + result.Files.Count + @"</p>
                    <p>You can now close this window and return to your application.</p>
                    <button class='button' onclick='window.close()'>Close Window</button>
                    <button class='button' onclick='location.href=""/swagger/index.html""'>Open API Docs</button>
                </div>
                <script>
                    // Try to close the window automatically after 3 seconds
                    setTimeout(function() {
                        try { window.close(); } 
                        catch(e) { /* ignore */ }
                    }, 3000);
                </script>
            </body>
            </html>", "text/html");
            }
            catch (System.Exception ex)
            {
                return Content(@"
            <html>
            <head><title>Error</title></head>
            <body style='font-family: Arial, sans-serif; padding: 20px;'>
                <h1>Authentication Failed</h1>
                <p><strong>Error:</strong> " + ex.Message + @"</p>
                <p><strong>Code used:</strong> " + code + @"</p>
                <button onclick='window.close()'>Close</button>
            </body>
            </html>", "text/html");
            }
        }
        private string CleanAuthorizationCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return code;

            // Remove common corruptions
            code = code.Replace("&", "")
                       .Replace("$", "")
                       .Replace("-", "")
                       .Replace("\n", "")
                       .Replace("\r", "")
                       .Replace(" ", "");

            return code;
        }

        [HttpGet("debug")]
        public IActionResult DebugEndpoint()
        {
            var debugInfo = new
            {
                Timestamp = DateTime.Now,
                Headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                QueryString = Request.QueryString.Value,
                Host = Request.Host.Value,
                Scheme = Request.Scheme,
                IsHttps = Request.IsHttps
            };

            return Ok(debugInfo);
        }
        [HttpGet("test-token")]
        public async Task<IActionResult> TestToken()
        {
            var hasToken = _authService.HasStoredToken();

            if (!hasToken)
            {
                return Ok(new { status = "No token found" });
            }

            try
            {
                var driveService = await _authService.TryLoadExistingTokenAsync();
                var request = driveService.Files.List();
                request.PageSize = 5;
                var result = await request.ExecuteAsync();

                return Ok(new
                {
                    status = "Token is valid and working!",
                    files_count = result.Files.Count,
                    sample_files = result.Files.Select(f => f.Name)
                });
            }
            catch (Exception ex)
            {
                return Ok(new
                {
                    status = "Token exists but is invalid",
                    error = ex.Message
                });
            }
        }
        [HttpGet("setup")]
            public IActionResult GetSetupInfo()
            {
                try
                {
                    var authUrl = _authService.GetAuthorizationUrl();
                    var configInfo = _authService.GetConfigInfo();

                    return Ok(new
                    {
                        success = true,
                        message = "🔧 First-time setup - Follow these steps:",
                        steps = new[]
                        {
                        "1. Click the auth_url below to start OAuth flow",
                        "2. Login with Google and accept permissions",
                        "3. You'll be automatically redirected and authenticated",
                        "4. Return to your app to use the Drive API"
                    },
                        auth_url = authUrl,
                        config_info = configInfo
                    });
                }
                catch (System.Exception ex)
                {
                    return BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            }
        }
    }