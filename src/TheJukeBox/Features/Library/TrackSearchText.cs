using System.Globalization;
using System.Text;
using TheJukeBox.Domain;

namespace TheJukeBox.Features.Library;

/// <summary>
/// Builds the normalised haystack used for substring search.
/// </summary>
/// <remarks>
/// <para>
/// Accents are folded so "Bjork" finds "Björk" and "Sigur Ros" finds "Sigur Rós", which is
/// what people actually type. Done once per change rather than per query across every row,
/// because SQLite has no full-text index here and normalising several columns for every
/// keystroke over thousands of rows is exactly the work that makes search feel slow.
/// </para>
/// <para>
/// Lifted out of <see cref="LibrarySynchroniser"/>, where it was private, when the tag
/// pass became a second writer of the same columns. Two implementations of this folding
/// would drift, and the moment they drift a track becomes findable by its filename-guessed
/// artist but not by its real one.
/// </para>
/// </remarks>
internal static class TrackSearchText
{
    /// <summary>Matches the column length configured in <c>TrackConfiguration</c>.</summary>
    private const int MaxLength = 2048;

    public static string Build(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        // Name is included last and deliberately: after tags are read the file name is
        // usually redundant, but people search for what they downloaded.
        var joined = string.Join(
            ' ',
            new[] { track.Title, track.Artist, track.Album, track.AlbumArtist, track.Name }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        var decomposed = joined.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToLowerInvariant(c));
            }
        }

        var result = folded.ToString().Normalize(NormalizationForm.FormC);

        return result.Length > MaxLength ? result[..MaxLength] : result;
    }
}
