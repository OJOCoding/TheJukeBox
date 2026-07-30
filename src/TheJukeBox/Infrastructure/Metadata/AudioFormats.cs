namespace TheJukeBox.Infrastructure.Metadata;

/// <summary>
/// Maps between audio file names, extensions, and MIME types.
/// </summary>
/// <remarks>
/// Formerly <c>TheJukeBox.API.FileExtensions</c>, whose name suggested a class of
/// extension methods. Renamed on the move that retired the <c>API</c> folder.
/// </remarks>
public static class AudioFormats
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".mp4", ".wma",
    };

    private static readonly Dictionary<string, string> ExtensionByMimeType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = ".mp3",
        ["audio/mp3"] = ".mp3",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/wave"] = ".wav",
        ["audio/ogg"] = ".ogg",
        ["audio/opus"] = ".opus",
        ["audio/flac"] = ".flac",
        ["audio/x-flac"] = ".flac",
        ["audio/mp4"] = ".m4a",
        ["audio/x-m4a"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/aacp"] = ".aac",
        ["audio/x-ms-wma"] = ".wma",
    };

    private static readonly Dictionary<string, string> MimeTypeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/opus",
        [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4",
        [".mp4"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".wma"] = "audio/x-ms-wma",
    };

    /// <summary>
    /// Best-effort extension for a Drive file, preferring the file name and falling back
    /// to the MIME type.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when neither source identifies a known audio
    /// format. The original implementation defaulted to <c>".mp3"</c>, which announced an
    /// unusually-typed FLAC to the browser as <c>audio/mpeg</c> and failed to decode.
    /// Admitting we do not know lets the caller send <c>application/octet-stream</c> and
    /// leave the browser to sniff, which actually works.
    /// </remarks>
    public static string? ResolveExtension(string? fileName, string? mimeType)
    {
        if (!string.IsNullOrEmpty(fileName))
        {
            var fromName = Path.GetExtension(fileName);

            if (Supported.Contains(fromName))
            {
                return fromName.ToLowerInvariant();
            }
        }

        if (!string.IsNullOrEmpty(mimeType) && ExtensionByMimeType.TryGetValue(mimeType, out var fromMime))
        {
            return fromMime;
        }

        return null;
    }

    /// <summary>The MIME type to serve for an extension, or octet-stream when unknown.</summary>
    public static string ContentTypeFor(string? extension) =>
        extension is not null && MimeTypeByExtension.TryGetValue(extension, out var mimeType)
            ? mimeType
            : "application/octet-stream";

    public static bool IsSupported(string? extension) =>
        extension is not null && Supported.Contains(extension);
}
