using TheJukeBox.Features.Library;
using Xunit.Abstractions;

namespace TheJukeBox.Tests;

/// <summary>
/// The artist-key encoding, as a contract between the server and the browser.
/// </summary>
/// <remarks>
/// <para>
/// The album detail view links back to the artist and builds that key on the client, because the
/// album response already carries the artist name and adding a second key to the payload for one
/// link is not worth the API surface. That makes the encoding an algorithm implemented twice, in
/// two languages.
/// </para>
/// <para>
/// Duplication like that drifts silently: a mismatch is not an exception anywhere, it is one link
/// that 404s. So both sides assert the same checked-in fixtures --
/// <c>tests/js/group-key.test.mjs</c> holds the identical table. Change either implementation and
/// one of the two suites fails.
/// </para>
/// </remarks>
public class GroupKeyContractTests(ITestOutputHelper output)
{
    /// <summary>
    /// Base64url of "a" + the name in UTF-8, unpadded.
    /// </summary>
    /// <remarks>
    /// The leading "a" is the artist-kind marker. It keeps an artist key distinguishable from an
    /// album key, and it is what stops the key for an absent name encoding to an empty string --
    /// which would be an unreachable URL, and on an untagged library that group is most of the
    /// music.
    /// </remarks>
    public static readonly TheoryData<string, string> Fixtures = new()
    {
        { "Radiohead", "YVJhZGlvaGVhZA" },
        { "AC/DC", "YUFDL0RD" },
        { "Sigur Rós", "YVNpZ3VyIFLDs3M" },
        { "", "YQ" },
        { "100% Silk", "YTEwMCUgU2lsaw" },
        { "日本語", "YeaXpeacrOiqng" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void An_artist_key_matches_the_checked_in_fixture(string artist, string expected) =>
        Assert.Equal(expected, GroupKey.ForArtist(artist));

    /// <summary>
    /// Prints the current encodings, so correcting the fixtures after a deliberate change is a
    /// copy rather than a hand computation. Hand-computing base64 is exactly how a fixture ends up
    /// asserting the wrong thing on both sides at once.
    /// </summary>
    [Fact]
    public void Report_current_encodings()
    {
        foreach (var artist in new[] { "Radiohead", "AC/DC", "Sigur Rós", "", "100% Silk", "日本語" })
        {
            output.WriteLine($"{artist,-16} => {GroupKey.ForArtist(artist)}");
        }
    }
}
