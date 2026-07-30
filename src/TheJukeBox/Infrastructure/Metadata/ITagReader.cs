namespace TheJukeBox.Infrastructure.Metadata;

/// <summary>
/// Cover art as it was embedded in the file, before anything re-encodes it.
/// </summary>
/// <param name="Bytes">
/// The original bytes. Hashed as-is, which is what makes deduplication work: twelve tracks
/// ripped from one album carry byte-identical pictures, so hashing before any re-encode
/// collapses an album to a single stored image with no album-grouping logic anywhere.
/// </param>
public sealed record EmbeddedArt(byte[] Bytes, string? MimeType);

/// <summary>
/// What a tag reader managed to extract. Every field is optional by design.
/// </summary>
/// <remarks>
/// A record of nullables rather than a partially-populated <c>Track</c>: the reader has no
/// business knowing about persistence, and the enricher needs to distinguish "the tag said
/// nothing" from "the tag said empty string" to decide whether the filename guess still
/// wins.
/// </remarks>
public sealed record TrackTags
{
    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public string? AlbumArtist { get; init; }

    public int? TrackNumber { get; init; }

    public int? DiscNumber { get; init; }

    public int? Year { get; init; }

    public string? Genre { get; init; }

    public int? DurationSeconds { get; init; }

    public EmbeddedArt? Art { get; init; }

    /// <summary>The tag formats actually found, for logging. Empty means an untagged file.</summary>
    public string? Formats { get; init; }

    /// <summary>
    /// Whether anything worth storing was found.
    /// </summary>
    /// <remarks>
    /// Duration alone counts. A file with no text tags but a readable duration is still
    /// better described by the reader than by its name, and the transport needs the
    /// duration to render anything other than a dash.
    /// </remarks>
    public bool HasAnything =>
        !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(Artist)
        || !string.IsNullOrWhiteSpace(Album)
        || !string.IsNullOrWhiteSpace(AlbumArtist)
        || !string.IsNullOrWhiteSpace(Genre)
        || TrackNumber is > 0
        || DiscNumber is > 0
        || Year is > 0
        || DurationSeconds is > 0
        || Art is not null;
}

/// <summary>
/// Reads embedded tags out of a seekable audio stream.
/// </summary>
/// <remarks>
/// Narrow on purpose. The library behind it was a genuinely open question (see
/// <see cref="AtlTagReader"/> for the licence and capability trade-off that settled it),
/// and a two-method seam means swapping it is one registration change rather than a
/// rewrite. It also means the extraction pipeline can be tested against a stub that
/// returns fixed tags, and the reader can be tested against local files, without either
/// test needing the other half.
/// </remarks>
public interface ITagReader
{
    /// <summary>
    /// Attempts to read <paramref name="seekable"/>.
    /// </summary>
    /// <param name="seekable">
    /// A stream with <see cref="Stream.CanSeek"/> true and a known
    /// <see cref="Stream.Length"/>. Forward-only streams are rejected rather than buffered.
    /// </param>
    /// <param name="fileName">Used only as a format hint and as a last-resort title.</param>
    /// <param name="mimeType">Format hint, when Drive reported a useful one.</param>
    /// <param name="tags">The extraction, or an empty instance on failure.</param>
    /// <returns>
    /// False when the stream could not be parsed at all. A parsable file that simply
    /// carries no tags returns true with <see cref="TrackTags.HasAnything"/> false, because
    /// those are different situations: one is worth retrying, the other is not.
    /// </returns>
    bool TryRead(Stream seekable, string fileName, string? mimeType, out TrackTags tags);
}
