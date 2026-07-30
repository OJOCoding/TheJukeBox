namespace TheJukeBox.Infrastructure.Metadata;

/// <summary>
/// Identifies embedded cover art by its own magic bytes.
/// </summary>
/// <remarks>
/// The MIME type written in an audio tag is attacker-influenced data: it is whatever
/// whoever produced the file chose to put there, and this application later returns it as a
/// <c>Content-Type</c> header. A tag declaring <c>text/html</c> over bytes the browser then
/// executes in the app's own origin is a stored XSS, and the same tag declaring
/// <c>image/svg+xml</c> is the same problem with a nicer name, because SVG carries script.
/// So the declared type is discarded entirely and the format is decided from the bytes,
/// against a raster-only allow-list.
/// </remarks>
internal static class ImageFormats
{
    /// <summary>
    /// The MIME type for <paramref name="bytes"/>, or null when they are not a supported
    /// raster image.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            return null;
        }

        // JPEG: SOI marker followed by any marker byte.
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        // Assigned to a typed local first: a collection expression passed straight to
        // SequenceEqual has no target type to infer the element from, and the compiler
        // resolves it to the int overload.
        ReadOnlySpan<byte> pngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

        if (bytes[..8].SequenceEqual(pngSignature))
        {
            return "image/png";
        }

        if (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))
        {
            return "image/gif";
        }

        // WebP is a RIFF container; the form type at offset 8 is what distinguishes it
        // from a WAV file, which is also RIFF.
        if (bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return "image/bmp";
        }

        // Deliberately absent: SVG and anything else textual. An <svg> can contain a
        // <script>, so serving one from this origin would execute in the session's context.
        return null;
    }
}
