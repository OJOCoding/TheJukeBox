using TheJukeBox.Infrastructure.Metadata;

namespace TheJukeBox.Tests;

public class FilenameMetadataParserTests
{
    [Theory]
    // Plain "Artist - Title", the common case.
    [InlineData("Burial - Archangel.mp3", "Archangel", "Burial", null, null)]
    // Leading track number, then artist and title.
    [InlineData("03 - Boards of Canada - Roygbiv.flac", "Roygbiv", "Boards of Canada", null, 3)]
    [InlineData("03. Boards of Canada - Roygbiv.flac", "Roygbiv", "Boards of Canada", null, 3)]
    // Four parts. Must beat the looser "Artist - Title" rule, which would otherwise
    // read the artist as "Aphex Twin" and the title as "SAW II - 04 - Blue Calx".
    [InlineData("Aphex Twin - SAW II - 04 - Blue Calx.mp3", "Blue Calx", "Aphex Twin", "SAW II", 4)]
    // Number and title only.
    [InlineData("07 Untrue.mp3", "Untrue", null, null, 7)]
    [InlineData("07_Untrue.mp3", "Untrue", null, null, 7)]
    [InlineData("12. Rue des Cascades.m4a", "Rue des Cascades", null, null, 12)]
    // Nothing parseable: the stem is the title, and no artist is invented.
    [InlineData("somerandomfile.mp3", "somerandomfile", null, null, null)]
    [InlineData("Windowlicker.flac", "Windowlicker", null, null, null)]
    public void Parses_common_naming_conventions(
        string fileName,
        string expectedTitle,
        string? expectedArtist,
        string? expectedAlbum,
        int? expectedTrackNumber)
    {
        var parsed = FilenameMetadataParser.Parse(fileName);

        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal(expectedArtist, parsed.Artist);
        Assert.Equal(expectedAlbum, parsed.Album);
        Assert.Equal(expectedTrackNumber, parsed.TrackNumber);
    }

    [Theory]
    [InlineData("Daft Punk - Around The World (Official Video).mp3", "Around The World")]
    [InlineData("Daft Punk - Around The World [Official Music Video].mp3", "Around The World")]
    [InlineData("Daft Punk - Around The World (Lyrics).mp3", "Around The World")]
    [InlineData("Daft Punk - Around The World (Audio).mp3", "Around The World")]
    [InlineData("Daft Punk - Around The World [HQ].mp3", "Around The World")]
    [InlineData("Daft Punk - Around The World 320kbps.mp3", "Around The World")]
    [InlineData("Daft Punk - Around The World [dQw4w9WgXcQ].mp3", "Around The World")]
    [InlineData("Daft Punk - Around The World (Official Video) [dQw4w9WgXcQ].mp3", "Around The World")]
    public void Strips_download_tool_litter(string fileName, string expectedTitle)
    {
        var parsed = FilenameMetadataParser.Parse(fileName);

        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal("Daft Punk", parsed.Artist);
    }

    /// <summary>
    /// The video-id rule is length-exact and anchored precisely so it does not eat
    /// legitimate bracketed titles, of which there are many.
    /// </summary>
    [Theory]
    [InlineData("Aphex Twin - Windowlicker [Mix].mp3", "Windowlicker [Mix]")]
    [InlineData("Squarepusher - Come On My Selector [Radio Edit].mp3", "Come On My Selector [Radio Edit]")]
    public void Keeps_meaningful_bracketed_text(string fileName, string expectedTitle) =>
        Assert.Equal(expectedTitle, FilenameMetadataParser.Parse(fileName).Title);

    [Fact]
    public void Uses_the_folder_as_the_album_when_the_name_does_not_say()
    {
        var parsed = FilenameMetadataParser.Parse("04 Idioteque.flac", "Kid A");

        Assert.Equal("Idioteque", parsed.Title);
        Assert.Equal("Kid A", parsed.Album);
        Assert.Equal(4, parsed.TrackNumber);
    }

    [Fact]
    public void Splits_an_artist_and_album_folder()
    {
        var parsed = FilenameMetadataParser.Parse("04 Idioteque.flac", "Radiohead - Kid A");

        Assert.Equal("Kid A", parsed.Album);
        Assert.Equal("Radiohead", parsed.Artist);
    }

    [Fact]
    public void Reads_a_year_from_the_folder_and_removes_it_from_the_album()
    {
        var parsed = FilenameMetadataParser.Parse("01 Everything In Its Right Place.flac", "Kid A (2000)");

        Assert.Equal("Kid A", parsed.Album);
        Assert.Equal(2000, parsed.Year);
    }

    /// <summary>
    /// The file name is the more specific source, so it must not be overridden by folder
    /// context. A compilation folder would otherwise relabel every track's artist.
    /// </summary>
    [Fact]
    public void Filename_beats_folder_context()
    {
        var parsed = FilenameMetadataParser.Parse(
            "Aphex Twin - SAW II - 04 - Blue Calx.mp3",
            "Various Artists - Best Of 1994");

        Assert.Equal("Aphex Twin", parsed.Artist);
        Assert.Equal("SAW II", parsed.Album);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".mp3")]
    [InlineData("___.mp3")]
    [InlineData("---")]
    public void Degrades_to_Unknown_rather_than_throwing(string? fileName)
    {
        var parsed = FilenameMetadataParser.Parse(fileName);

        Assert.False(string.IsNullOrWhiteSpace(parsed.Title));
        Assert.Null(parsed.Artist);
    }

    /// <summary>
    /// A wrong guess is worse than none: a fabricated artist scatters an album across
    /// the browse view, whereas a missing one merely groups it under "Unknown".
    /// </summary>
    [Theory]
    [InlineData("Untitled.mp3")]
    [InlineData("Track 5.mp3")]
    [InlineData("recording.wav")]
    public void Never_invents_an_artist(string fileName) =>
        Assert.Null(FilenameMetadataParser.Parse(fileName).Artist);

    [Fact]
    public void Track_number_zero_is_not_a_track_number() =>
        Assert.Null(FilenameMetadataParser.Parse("00 - Intro.mp3").TrackNumber);
}
