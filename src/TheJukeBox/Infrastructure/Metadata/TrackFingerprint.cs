namespace TheJukeBox.Infrastructure.Metadata;

/// <summary>
/// Identifies the bytes a track's metadata was read from.
/// </summary>
/// <remarks>
/// The point of a content fingerprint rather than a timestamp is that renaming a file must
/// not trigger re-extraction while editing it must. A library of five thousand files
/// re-read on every sync would make the tag pass cost as much as a full download every
/// time, which is the failure this whole phase is designed to avoid.
/// </remarks>
internal static class TrackFingerprint
{
    /// <summary>
    /// Drive's MD5 when it supplies one, otherwise modified time plus size.
    /// </summary>
    /// <remarks>
    /// Drive omits the MD5 for Google-native documents and occasionally for very large
    /// files. The fallback is weaker (an edit that preserves both size and mtime is
    /// invisible to it) but that combination essentially does not occur for audio files,
    /// and the alternative is re-reading those files forever.
    /// </remarks>
    public static string For(string? md5Checksum, DateTimeOffset? modifiedAtUtc, long sizeBytes) =>
        md5Checksum is { Length: > 0 } md5
            ? md5
            : $"{modifiedAtUtc:O}|{sizeBytes}";
}
