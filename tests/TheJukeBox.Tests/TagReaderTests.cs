using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using TheJukeBox.Infrastructure.Metadata;

namespace TheJukeBox.Tests;

/// <summary>
/// Exercises the real tag reader against real containers.
/// </summary>
/// <remarks>
/// Until these existed the extraction pipeline had well-tested plumbing and an entirely
/// unproven payload: <see cref="DriveRandomAccessStream"/> was covered, but nothing had ever
/// read an actual tag through it. The fixtures are built from the format specifications in
/// <see cref="AudioFixtures"/>, so the expected values do not come from the implementation
/// under test.
/// </remarks>
public class TagReaderTests
{
    private static AtlTagReader Reader() => new(NullLogger<AtlTagReader>.Instance);

    private static TrackTags Read(byte[] content, string fileName, string? mimeType = null)
    {
        using var stream = new MemoryStream(content, writable: false);

        Assert.True(
            Reader().TryRead(stream, fileName, mimeType, out var tags),
            $"the reader could not parse {fileName}");

        return tags;
    }

    [Fact]
    public void Reads_vorbis_comments_from_flac()
    {
        var tags = Read(AudioFixtures.Flac(), "track.flac", "audio/flac");

        Assert.Equal(AudioFixtures.Title, tags.Title);
        Assert.Equal(AudioFixtures.Artist, tags.Artist);
        Assert.Equal(AudioFixtures.Album, tags.Album);
        Assert.Equal(AudioFixtures.TrackNumber, tags.TrackNumber);
        Assert.Equal(AudioFixtures.Year, tags.Year);
        Assert.True(tags.HasAnything);
    }

    /// <summary>
    /// FLAC's STREAMINFO states the total sample count, so the duration is exact rather
    /// than inferred from a bitrate. Asserting it proves the reader is reading the header
    /// and not guessing from the file size.
    /// </summary>
    [Theory]
    [InlineData(222)]
    [InlineData(37)]
    [InlineData(600)]
    public void Reads_an_exact_duration_from_flac_streaminfo(int seconds)
    {
        var tags = Read(AudioFixtures.Flac(durationSeconds: seconds), "track.flac", "audio/flac");

        Assert.NotNull(tags.DurationSeconds);

        // One second of tolerance for rounding between samples and seconds.
        Assert.InRange(tags.DurationSeconds!.Value, seconds - 1, seconds + 1);
    }

    [Fact]
    public void Reads_id3v2_frames_from_mp3()
    {
        var tags = Read(AudioFixtures.Mp3WithId3v2(), "track.mp3", "audio/mpeg");

        Assert.Equal(AudioFixtures.Title, tags.Title);
        Assert.Equal(AudioFixtures.Artist, tags.Artist);
        Assert.Equal(AudioFixtures.Album, tags.Album);
        Assert.Equal(AudioFixtures.TrackNumber, tags.TrackNumber);
    }

    /// <summary>
    /// ID3v1 occupies the final 128 bytes. Reading it is the concrete case for a seekable
    /// stream over a remote file: a reader that could only prefetch a head block would find
    /// nothing here, and would find nothing in a non-faststart M4A for the same reason.
    /// </summary>
    [Fact]
    public void Reads_id3v1_from_the_end_of_the_file()
    {
        var tags = Read(AudioFixtures.Mp3WithId3v1Only(), "track.mp3", "audio/mpeg");

        Assert.Equal(AudioFixtures.Title, tags.Title);
        Assert.Equal(AudioFixtures.Artist, tags.Artist);

        // ID3v1 fields are fixed at 30 bytes with no continuation, so this album name (32
        // characters) is genuinely truncated in the file. Asserting the full string here
        // would be asserting that the reader invents data it was never given. This is a
        // real limitation of the format and precisely why ID3v2 exists.
        var truncated = AudioFixtures.Album[..30].TrimEnd();

        Assert.Equal(truncated, tags.Album);
        Assert.True(AudioFixtures.Album.Length > 30, "the fixture must exceed 30 chars to test truncation");
    }

    [Fact]
    public void Extracts_embedded_cover_art_from_flac()
    {
        var tags = Read(AudioFixtures.Flac(withPicture: true), "track.flac", "audio/flac");

        Assert.NotNull(tags.Art);
        Assert.Equal(AudioFixtures.CoverJpeg, tags.Art!.Bytes);
    }

    [Fact]
    public void Extracts_embedded_cover_art_from_mp3()
    {
        var tags = Read(AudioFixtures.Mp3WithId3v2(withPicture: true), "track.mp3", "audio/mpeg");

        Assert.NotNull(tags.Art);
        Assert.Equal(AudioFixtures.CoverJpeg, tags.Art!.Bytes);
    }

    /// <summary>
    /// The property album-art deduplication depends on. Two different tracks carrying the
    /// same picture must yield byte-identical art, so hashing collapses an album to one
    /// stored row without any album-grouping logic.
    /// </summary>
    [Fact]
    public void The_same_picture_in_different_files_hashes_identically()
    {
        var fromFlac = Read(AudioFixtures.Flac(withPicture: true), "a.flac", "audio/flac").Art;
        var fromMp3 = Read(AudioFixtures.Mp3WithId3v2(withPicture: true), "b.mp3", "audio/mpeg").Art;

        Assert.NotNull(fromFlac);
        Assert.NotNull(fromMp3);

        var first = Convert.ToHexString(SHA256.HashData(fromFlac!.Bytes));
        var second = Convert.ToHexString(SHA256.HashData(fromMp3!.Bytes));

        Assert.Equal(first, second);
    }

    /// <summary>
    /// A parsable file with no tags is not a failure. The distinction matters: the reader
    /// returning false means "could not read this", which is worth logging and retrying,
    /// whereas no tags simply means the filename guess stands.
    /// </summary>
    [Fact]
    public void An_untagged_but_valid_file_succeeds_with_nothing_to_report()
    {
        using var stream = new MemoryStream(AudioFixtures.Mp3Untagged(), writable: false);

        Assert.True(Reader().TryRead(stream, "untitled.mp3", "audio/mpeg", out var tags));

        Assert.Null(tags.Title);
        Assert.Null(tags.Artist);
        Assert.Null(tags.Album);
    }

    [Fact]
    public void Rubbish_is_reported_as_unreadable_rather_than_throwing()
    {
        using var stream = new MemoryStream(new byte[4096], writable: false);

        // Never throws: one unreadable file must not fail a sync pass over thousands.
        var read = Reader().TryRead(stream, "notaudio.mp3", "audio/mpeg", out var tags);

        Assert.False(read && tags.HasAnything);
    }

    [Fact]
    public void A_truncated_file_does_not_throw()
    {
        var truncated = AudioFixtures.Flac()[..20];

        using var stream = new MemoryStream(truncated, writable: false);

        var read = Reader().TryRead(stream, "truncated.flac", "audio/flac", out var tags);

        Assert.False(read && tags.HasAnything);
    }
}

/// <summary>
/// The pipeline end to end: real tags, read through the same ranged stream the enricher
/// uses against Drive.
/// </summary>
/// <remarks>
/// This is the combination that had never been exercised. Each half was covered; the seam
/// between them was not, and the seam is where "reads tags without downloading whole files"
/// is either true or false.
/// </remarks>
public class TagReadingThroughRangeStreamTests
{
    private static async Task<(TrackTags Tags, CountingRangeReader Reader)> ReadAsync(
        byte[] content,
        string fileName,
        string? mimeType)
    {
        var reader = new CountingRangeReader(content);

        await using var stream = await DriveRandomAccessStream.OpenAsync(reader, content.Length);

        var tagReader = new AtlTagReader(NullLogger<AtlTagReader>.Instance);

        Assert.True(tagReader.TryRead(stream, fileName, mimeType, out var tags), $"could not parse {fileName}");

        return (tags, reader);
    }

    [Fact]
    public async Task Reads_flac_tags_over_ranged_requests()
    {
        var (tags, _) = await ReadAsync(AudioFixtures.Flac(withPicture: true), "track.flac", "audio/flac");

        Assert.Equal(AudioFixtures.Title, tags.Title);
        Assert.Equal(AudioFixtures.Artist, tags.Artist);
        Assert.NotNull(tags.Art);
    }

    [Fact]
    public async Task Reads_id3v2_tags_over_ranged_requests()
    {
        var (tags, _) = await ReadAsync(AudioFixtures.Mp3WithId3v2(), "track.mp3", "audio/mpeg");

        Assert.Equal(AudioFixtures.Title, tags.Title);
        Assert.Equal(AudioFixtures.Artist, tags.Artist);
    }

    /// <summary>
    /// The whole justification for the design, measured rather than asserted. A file
    /// comfortably larger than the whole-file threshold must still be read in a handful of
    /// requests, and must not be pulled down in full.
    /// </summary>
    [Fact]
    public async Task A_large_file_is_read_without_downloading_it()
    {
        // ~3.4 MB of frames, well past the 1 MiB whole-file shortcut.
        var content = AudioFixtures.Mp3WithId3v2(frames: 8192);

        Assert.True(content.Length > DriveRandomAccessStream.WholeFileThreshold * 3,
            "the fixture must exceed the whole-file threshold or this proves nothing");

        var (tags, reader) = await ReadAsync(content, "big.mp3", "audio/mpeg");

        Assert.Equal(AudioFixtures.Title, tags.Title);

        Assert.True(
            reader.Requests <= DriveRandomAccessStream.MaxRequests,
            $"took {reader.Requests} requests, budget is {DriveRandomAccessStream.MaxRequests}");

        Assert.True(
            reader.BytesServed < content.Length,
            $"served {reader.BytesServed} of {content.Length} bytes: the file was effectively downloaded");
    }

    /// <summary>
    /// The tail case, measured. ID3v1 sits in the last 128 bytes of a large file, so a
    /// correct reader fetches the head and the tail and nothing in between.
    /// </summary>
    [Fact]
    public async Task Reading_a_trailing_tag_fetches_only_the_ends()
    {
        var content = AudioFixtures.Mp3WithId3v1Only(frames: 8192);

        var (tags, reader) = await ReadAsync(content, "tail.mp3", "audio/mpeg");

        Assert.Equal(AudioFixtures.Title, tags.Title);

        Assert.True(
            reader.BytesServed < content.Length / 2,
            $"served {reader.BytesServed} of {content.Length} bytes to read a 128-byte trailing tag");
    }
}
