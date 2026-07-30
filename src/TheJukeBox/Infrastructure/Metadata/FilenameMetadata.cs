using System.Text.RegularExpressions;

namespace TheJukeBox.Infrastructure.Metadata;

/// <summary>
/// Metadata guessed from a file's name and the folder it sits in.
/// </summary>
public sealed record FilenameMetadata(
    string Title,
    string? Artist,
    string? Album,
    int? TrackNumber,
    int? Year);

/// <summary>
/// Derives artist, album, track number, and title from a file name.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of a library's metadata until tag reading arrives in B1, and it
/// stays the fallback afterwards for the many files that carry no tags at all. Getting
/// it reasonable is the difference between a browsable library and a list of file names,
/// which is what the UI showed before.
/// </para>
/// <para>
/// Ordered patterns, first match wins, most specific first. Deliberately conservative:
/// a wrong guess is worse than no guess, because a wrong artist scatters an album
/// across the browse view while a missing one merely leaves it under "Unknown".
/// </para>
/// </remarks>
public static partial class FilenameMetadataParser
{
    /// <summary>
    /// "Artist - Album - 04 - Title". Must be tried first: its prefix also matches the
    /// looser "Artist - Title" pattern, which would swallow the album and number.
    /// </summary>
    [GeneratedRegex(
        @"^(?<artist>.+?)\s+-\s+(?<album>.+?)\s+-\s+(?<num>\d{1,3})\s+-\s+(?<title>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArtistAlbumNumberTitle { get; }

    /// <summary>"03 - Boards of Canada - Roygbiv".</summary>
    [GeneratedRegex(
        @"^(?<num>\d{1,3})\s*[-._)]\s*(?<artist>.+?)\s+-\s+(?<title>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumberArtistTitle { get; }

    /// <summary>"Burial - Archangel", the overwhelmingly common case.</summary>
    [GeneratedRegex(
        @"^(?<artist>.+?)\s+-\s+(?<title>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArtistTitle { get; }

    /// <summary>"07 Untrue", "07. Untrue", "07_Untrue".</summary>
    [GeneratedRegex(
        @"^(?<num>\d{1,3})[\s.\-_]+(?<title>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumberTitle { get; }

    /// <summary>
    /// Noise that download tools add. Extremely common in libraries assembled from
    /// YouTube, and it otherwise ends up inside the track title.
    /// </summary>
    [GeneratedRegex(
        @"\s*[\(\[](?:"
        + @"official\s*(?:music\s*)?(?:video|audio|lyric\s*video)?|"
        + @"lyrics?|audio|hd|hq|4k|full\s*album|free\s*download|"
        + @"visuali[sz]er|music\s*video|explicit|remastered\s*\d{0,4}"
        + @")[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParentheticalNoise { get; }

    /// <summary>Bitrate and format litter: " 320kbps", "-128 kbps".</summary>
    [GeneratedRegex(@"[\s\-_]*\b\d{2,4}\s*kbps\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BitrateNoise { get; }

    /// <summary>
    /// A trailing 11-character YouTube id in brackets, as yt-dlp appends by default.
    /// </summary>
    /// <remarks>
    /// Deliberately anchored and length-exact. A looser rule would eat legitimate
    /// bracketed text, and plenty of real titles end in brackets ("Windowlicker [Mix]").
    /// </remarks>
    [GeneratedRegex(@"\s*[\[\(][A-Za-z0-9_-]{11}[\]\)]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingVideoId { get; }

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedWhitespace { get; }

    /// <summary>A year in brackets or parentheses, as folder names often carry.</summary>
    [GeneratedRegex(@"[\(\[](?<year>19\d{2}|20\d{2})[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedYear { get; }

    /// <summary>
    /// Parses <paramref name="fileName"/>, falling back to
    /// <paramref name="parentFolderName"/> for the album.
    /// </summary>
    public static FilenameMetadata Parse(string? fileName, string? parentFolderName = null)
    {
        var stem = Clean(Path.GetFileNameWithoutExtension(fileName ?? string.Empty));

        if (stem.Length == 0)
        {
            return new FilenameMetadata("Unknown", null, null, null, null);
        }

        var parsed = Match(stem);

        var (folderAlbum, folderArtist, folderYear) = FromFolder(parentFolderName);

        return parsed with
        {
            // Folder context only fills gaps; anything the file name stated wins,
            // because it is the more specific source.
            Album = parsed.Album ?? folderAlbum,
            Artist = parsed.Artist ?? folderArtist,
            Year = parsed.Year ?? folderYear,
        };
    }

    private static FilenameMetadata Match(string stem)
    {
        var match = ArtistAlbumNumberTitle.Match(stem);

        if (match.Success)
        {
            return new FilenameMetadata(
                Group(match, "title") ?? stem,
                Group(match, "artist"),
                Group(match, "album"),
                Number(match),
                null);
        }

        match = NumberArtistTitle.Match(stem);

        if (match.Success)
        {
            return new FilenameMetadata(
                Group(match, "title") ?? stem,
                Group(match, "artist"),
                null,
                Number(match),
                null);
        }

        match = ArtistTitle.Match(stem);

        if (match.Success)
        {
            return new FilenameMetadata(
                Group(match, "title") ?? stem,
                Group(match, "artist"),
                null,
                null,
                null);
        }

        match = NumberTitle.Match(stem);

        if (match.Success)
        {
            return new FilenameMetadata(Group(match, "title") ?? stem, null, null, Number(match), null);
        }

        return new FilenameMetadata(stem, null, null, null, null);
    }

    /// <summary>
    /// Reads an album, and possibly an artist and year, from the containing folder.
    /// </summary>
    /// <remarks>
    /// Music libraries are overwhelmingly one-folder-per-album, so the folder name is
    /// the single most reliable album signal available without reading tags.
    /// </remarks>
    private static (string? Album, string? Artist, int? Year) FromFolder(string? parentFolderName)
    {
        var folder = Clean(parentFolderName ?? string.Empty);

        if (folder.Length == 0)
        {
            return (null, null, null);
        }

        int? year = null;

        var yearMatch = BracketedYear.Match(folder);

        if (yearMatch.Success)
        {
            year = int.Parse(yearMatch.Groups["year"].ValueSpan, provider: null);
            folder = Clean(folder.Remove(yearMatch.Index, yearMatch.Length));
        }

        // "Aphex Twin - Selected Ambient Works" names both.
        var split = ArtistTitle.Match(folder);

        return split.Success
            ? (Group(split, "title"), Group(split, "artist"), year)
            : (folder, null, year);
    }

    /// <summary>Strips download litter and normalises separators.</summary>
    private static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // The video id is removed first: it is anchored to the end, and stripping
        // parenthetical noise ahead of it can move the anchor.
        var cleaned = TrailingVideoId.Replace(value, string.Empty);

        cleaned = ParentheticalNoise.Replace(cleaned, string.Empty);
        cleaned = BitrateNoise.Replace(cleaned, string.Empty);

        // Underscores stand in for spaces in filenames from almost every source.
        cleaned = cleaned.Replace('_', ' ');

        cleaned = RepeatedWhitespace.Replace(cleaned, " ");

        return cleaned.Trim(' ', '-', '.', '_');
    }

    private static string? Group(Match match, string name)
    {
        var value = match.Groups[name].Value.Trim();

        return value.Length == 0 ? null : value;
    }

    private static int? Number(Match match) =>
        int.TryParse(match.Groups["num"].ValueSpan, out var number) && number > 0 ? number : null;
}
