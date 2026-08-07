namespace TheJukeBox.Domain;

/// <summary>
/// One audio file in a user's library, as this instance last saw it.
/// </summary>
/// <remarks>
/// The cached library. Previously every page load listed Drive live, which cost 100
/// quota units per request, could not be searched or sorted, silently truncated at 100
/// files, and made the app unusable offline. Persisting the listing is what lets A6
/// resolve a stream's content type without a Drive call, and what makes the library
/// browsable while Drive is unreachable.
/// </remarks>
public sealed class Track
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>Drive's identifier. Unique per user, not globally.</summary>
    /// <remarks>
    /// Two users can legitimately have the same file id if a file is shared with both,
    /// so the uniqueness constraint is on the pair.
    /// </remarks>
    public string DriveFileId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? MimeType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>
    /// Drive's MD5, when it supplies one.
    /// </summary>
    /// <remarks>
    /// Serves two later purposes: deciding whether a file's content actually changed
    /// (so tag extraction is not redone after a rename), and as a cache key that
    /// deduplicates the same file held by different users.
    /// </remarks>
    public string? Md5Checksum { get; set; }

    /// <summary>Drive's revision id, used as the HTTP ETag when streaming.</summary>
    public string? HeadRevisionId { get; set; }

    public string? ParentFolderId { get; set; }

    /// <summary>
    /// Denormalised on purpose: it is the best available album name when a file carries
    /// no tags, and joining to fetch it for every row in a large library is not worth it.
    /// </summary>
    public string? ParentFolderName { get; set; }

    public DateTimeOffset? DriveCreatedAtUtc { get; set; }

    public DateTimeOffset? DriveModifiedAtUtc { get; set; }

    public string? FileExtension { get; set; }

    // --- Metadata. Filename-derived in A5, tag-derived in B1. ---

    public string Title { get; set; } = string.Empty;

    public string? Artist { get; set; }

    public string? Album { get; set; }

    public string? AlbumArtist { get; set; }

    public int? TrackNumber { get; set; }

    public int? DiscNumber { get; set; }

    public int? Year { get; set; }

    public string? Genre { get; set; }

    public int? DurationSeconds { get; set; }

    /// <summary>Where the metadata above came from, so the UI can mark guesses.</summary>
    public MetadataSource MetadataSource { get; set; } = MetadataSource.None;

    /// <summary>
    /// Identifies the content the metadata was read from, so a rename does not trigger
    /// re-extraction but an edit does.
    /// </summary>
    public string? MetadataFingerprint { get; set; }

    public DateTimeOffset? MetadataExtractedAtUtc { get; set; }

    /// <summary>Content hash of the embedded cover image, set in B1.</summary>
    public string? ArtSha256 { get; set; }

    // --- Online lookup, for what the file itself did not carry. ---

    /// <summary>
    /// When an online lookup was last attempted, successful or not.
    /// </summary>
    /// <remarks>
    /// Records the attempt rather than the outcome, because the failures are what need
    /// remembering. Without this, every sync re-queries every unmatched track at one request per
    /// second, forever.
    /// </remarks>
    public DateTimeOffset? OnlineLookupAtUtc { get; set; }

    /// <summary>
    /// The MusicBrainz recording this track was matched to, if any.
    /// </summary>
    /// <remarks>
    /// Kept so a wrong match can be recognised and explained rather than guessed at, and so a
    /// future feature can ask MusicBrainz for more about a track without matching it again. Null
    /// after a lookup that found nothing confident enough.
    /// </remarks>
    public string? MusicBrainzRecordingId { get; set; }

    /// <summary>
    /// Lowercased, accent-folded haystack for substring search.
    /// </summary>
    /// <remarks>
    /// Precomputed at sync time rather than built per query, because SQLite has no
    /// full-text index here and normalising several columns for every keystroke across
    /// thousands of rows is exactly the work that makes search feel slow.
    /// </remarks>
    public string SearchText { get; set; } = string.Empty;

    public DateTimeOffset AddedAtUtc { get; set; }

    // --- Listening counters, maintained alongside PlayHistory in C2. ---

    /// <summary>
    /// How many times this track has been listened to.
    /// </summary>
    /// <remarks>
    /// Denormalised from <see cref="PlayHistory"/> on purpose. Sorting a library by play
    /// count from the log means grouping the whole history table on every request, and it
    /// would answer wrongly once history is pruned. A counter is one extra write per listen
    /// -- of which there is at most one every few minutes per user -- and makes the sort an
    /// index seek.
    /// </remarks>
    public int PlayCount { get; set; }

    /// <summary>When this track was last listened to, or null if never.</summary>
    public DateTimeOffset? LastPlayedAtUtc { get; set; }

    /// <summary>
    /// Soft delete. Set when the file is trashed, removed, or retyped away from audio.
    /// </summary>
    /// <remarks>
    /// Soft rather than hard so a playlist entry or play-history row referencing a file
    /// the user briefly trashed does not vanish, and so an accidental un-trash restores
    /// the track rather than creating a duplicate with fresh metadata.
    /// </remarks>
    public DateTimeOffset? DeletedAtUtc { get; set; }

    /// <summary>
    /// Bumped on every change, so a client can ask "what changed since N?".
    /// </summary>
    /// <remarks>
    /// A per-user monotonic counter rather than a timestamp: two rows written in the
    /// same millisecond would be indistinguishable by time, and a client polling on a
    /// timestamp boundary would either miss one or fetch it twice.
    /// </remarks>
    public long RowVersion { get; set; }

    public bool IsAvailable => DeletedAtUtc is null;
}

/// <summary>Where a track's metadata came from.</summary>
public enum MetadataSource
{
    /// <summary>Nothing beyond what Drive reported; the title is the file name.</summary>
    None = 0,

    /// <summary>Guessed by parsing the file name and its folder.</summary>
    Filename = 1,

    /// <summary>Read from tags embedded in the file itself.</summary>
    Tags = 2,

    /// <summary>
    /// Matched against an online database because the file carried no usable tags.
    /// </summary>
    /// <remarks>
    /// Ranked above <see cref="Filename"/> and below <see cref="Tags"/>, which is exactly how much
    /// it should be trusted: better than parsing "03 - track.mp3", but still a guess about someone
    /// else's file, and never allowed to overwrite what the file itself declared.
    /// </remarks>
    Online = 3,
}
