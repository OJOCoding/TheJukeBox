using TheJukeBox.Configuration;

namespace TheJukeBox.Tests;

/// <summary>
/// Placeholder-credential detection.
/// </summary>
/// <remarks>
/// Startup validation can only prove a value is present; only Google can confirm it is real.
/// Without this check, a misconfigured instance sends the user to Google's "Error 401:
/// invalid_client" page, which does not say which end is at fault and offers no way back.
/// <para>
/// A false positive is the worse failure: telling someone their working credentials are broken
/// would be actively harmful, so the accepted cases below matter more than the rejected ones.
/// </para>
/// </remarks>
public class GoogleOAuthOptionsTests
{
    private static GoogleOAuthOptions With(string clientId) =>
        new() { ClientId = clientId, ClientSecret = "GOCSPX-someplausiblesecret" };

    [Theory]
    // Shapes Google actually issues: a numeric project number, a hyphen, a random-ish label.
    [InlineData("407408718192-6bn8sb1s1nabcdefghijk.apps.googleusercontent.com")]
    [InlineData("1044461980100-h8lq3tqmvp8nkfa2t4vs8lsqm3g0f6bt.apps.googleusercontent.com")]
    [InlineData("32555940559-9r9s1c4alg36erliucho9t52n32n6dgq.apps.googleusercontent.com")]
    public void Accepts_realistic_client_ids(string clientId) =>
        Assert.False(With(clientId).LooksLikePlaceholder, $"{clientId} was wrongly rejected");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // The exact value that produced the invalid_client page during development.
    [InlineData("000000000000-devplaceholder.apps.googleusercontent.com")]
    // Values from this repo's own documentation and templates, which are the ones a
    // self-hoster is most likely to paste by mistake.
    [InlineData("YOUR_CLIENT_ID.apps.googleusercontent.com")]
    [InlineData("000000000000-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx.apps.googleusercontent.com")]
    [InlineData("changeme.apps.googleusercontent.com")]
    [InlineData("your-client-id.apps.googleusercontent.com")]
    [InlineData("example.apps.googleusercontent.com")]
    // Not a client id at all: a common confusion with the project id or the secret.
    [InlineData("my-gcp-project")]
    [InlineData("GOCSPX-abcdefghijklmnop")]
    public void Rejects_placeholders_and_non_client_ids(string clientId) =>
        Assert.True(With(clientId).LooksLikePlaceholder, $"{clientId} was wrongly accepted");

    [Fact]
    public void Detection_is_case_insensitive() =>
        Assert.True(With("YOURCLIENTID.APPS.GOOGLEUSERCONTENT.COM").LooksLikePlaceholder);

    [Fact]
    public void The_redirect_uris_are_derived_from_the_public_base_url()
    {
        // Checked here because these two strings are what a self-hoster must paste into Cloud
        // Console verbatim, and a mismatch is the other cause of an opaque Google error.
        var options = new AppOptions { PublicBaseUrl = "https://music.example.com" };

        Assert.Equal("https://music.example.com/api/auth/callback", options.SignInRedirectUri);
        Assert.Equal("https://music.example.com/api/auth/drive/callback", options.DriveRedirectUri);
    }

    [Fact]
    public void A_trailing_slash_does_not_produce_a_double_slash()
    {
        var options = new AppOptions { PublicBaseUrl = "https://music.example.com/" };

        Assert.Equal("https://music.example.com/api/auth/callback", options.SignInRedirectUri);
    }
}
