namespace TheJukeBox.API;

/// <summary>
/// An audio file discovered in Google Drive. Superseded by the persisted
/// <c>Track</c> entity in A5.
/// </summary>
public sealed class AudioFile
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? MimeType { get; init; }

    public long Size { get; init; }

    /// <summary>
    /// <see cref="DateTimeOffset"/> rather than <see cref="DateTime"/> because Drive
    /// returns RFC 3339 timestamps with an offset, and the non-suffixed
    /// <c>CreatedTime</c>/<c>ModifiedTime</c> properties on the Drive model are
    /// obsolete and documented as behaving unexpectedly.
    /// </summary>
    public DateTimeOffset? CreatedTime { get; init; }

    public DateTimeOffset? ModifiedTime { get; init; }

    /// <summary>Relative URL this app serves the audio bytes from.</summary>
    public string? StreamUrl { get; init; }

    public string? WebContentLink { get; init; }

    /// <summary>Resolved audio extension, or null when the format is unrecognised.</summary>
    public string? FileExtension { get; init; }

    public string FormattedSize => ByteSize.Format(Size);

    public bool IsPlayable => FileExtensions.IsSupportedAudio(FileExtension);
}

/// <summary>A Drive file returned by a search, which may or may not be audio.</summary>
public sealed class DriveFile
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? MimeType { get; init; }

    public long Size { get; init; }

    public DateTimeOffset? CreatedTime { get; init; }

    public DateTimeOffset? ModifiedTime { get; init; }

    public bool IsAudio { get; init; }

    public string? WebContentLink { get; init; }

    public string FormattedSize => ByteSize.Format(Size);
}
