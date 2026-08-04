using System.Buffers.Text;
using System.Text;

namespace TheJukeBox.Features.Library;

/// <summary>
/// Reversible URL keys for albums and artists.
/// </summary>
/// <remarks>
/// <para>
/// Albums and artists are not rows. They are denormalised strings on <see cref="Domain.Track"/>,
/// so an album has no id to put in a URL. Three options were considered:
/// </para>
/// <list type="bullet">
/// <item>
/// A hash of the name. Short, but not reversible, so resolving one back to an album means
/// scanning every track and hashing as you go.
/// </item>
/// <item>
/// The raw name in the path. Album titles contain slashes, question marks, percent signs and
/// non-ASCII text; making that survive a round trip through a URL path segment, a client-side
/// router and a server route template correctly is more fiddly than it looks, and the failure
/// mode is "this one album is unreachable".
/// </item>
/// <item>
/// Base64url of the name. Reversible, needs no storage, survives any character, and is stable
/// across syncs because it is derived from the name itself. That is what this does.
/// </item>
/// </list>
/// <para>
/// An album key carries two fields, because an album is identified by its artist as well as its
/// title -- "Greatest Hits" is a different record for every band that made one.
/// </para>
/// </remarks>
internal static class GroupKey
{
    /// <summary>
    /// Marks a key as an artist key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two jobs, and both are load-bearing.
    /// </para>
    /// <para>
    /// First, it makes the encoding of an absent name non-empty. An untagged track has no artist
    /// and no album, and those groups still have to be reachable -- for a freshly synced library
    /// they may be most of it. Without a marker, base64url of an empty string is an empty string,
    /// so that group's URL would be <c>/artists/</c> with nothing in the segment, which neither
    /// the client router nor the server route will match. The group would render with a dead link.
    /// </para>
    /// <para>
    /// Second, it makes an artist key and an album key structurally different, so one cannot be
    /// passed where the other is expected and silently resolve to a group whose name happens to
    /// be the other's encoded form.
    /// </para>
    /// </remarks>
    private const char ArtistMarker = 'a';

    /// <summary>Marks a key as an album key. See <see cref="ArtistMarker"/>.</summary>
    private const char AlbumMarker = 'A';

    /// <summary>
    /// Separates the two halves of an album key.
    /// </summary>
    /// <remarks>
    /// U+001F, the ASCII unit separator, escaped rather than typed: a raw control character in
    /// source is invisible in review and does not survive every encoding round trip. It is chosen
    /// because it cannot appear in an ID3 text frame or a file name, so there is no escaping to
    /// get wrong. A value containing one anyway simply fails to match an album, rather than
    /// matching the wrong one.
    /// </remarks>
    private const char Separator = '\u001f';

    /// <summary>The longest key that will be decoded, so a hostile URL cannot allocate freely.</summary>
    private const int MaxEncodedLength = 2048;

    public static string ForArtist(string? artist) =>
        Encode($"{ArtistMarker}{artist}");

    public static string ForAlbum(string? albumArtist, string? album) =>
        Encode($"{AlbumMarker}{albumArtist}{Separator}{album}");

    /// <summary>
    /// Reads an artist key. Returns null when the key is not one this application produced.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception: the input is a URL segment, so a malformed one is an
    /// ordinary 404, not a server error worth a stack trace.
    /// </remarks>
    public static string? ReadArtist(string? key)
    {
        var decoded = Decode(key);

        return decoded is null || decoded.Length == 0 || decoded[0] != ArtistMarker
            ? null
            : decoded[1..];
    }

    /// <summary>Reads an album key into its artist and title halves.</summary>
    public static (string AlbumArtist, string Album)? ReadAlbum(string? key)
    {
        var decoded = Decode(key);

        if (decoded is null || decoded.Length == 0 || decoded[0] != AlbumMarker)
        {
            return null;
        }

        var body = decoded[1..];
        var at = body.IndexOf(Separator, StringComparison.Ordinal);

        // Both halves are required. A key with no separator was not produced by ForAlbum, so it
        // is rejected rather than guessed at.
        return at < 0
            ? null
            : (body[..at], body[(at + 1)..]);
    }

    /// <summary>
    /// Base64url without padding: the alphabet is already URL-safe, and dropping '=' keeps keys
    /// out of trouble with anything that treats it specially.
    /// </summary>
    private static string Encode(string value) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private static string? Decode(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxEncodedLength)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(key));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
