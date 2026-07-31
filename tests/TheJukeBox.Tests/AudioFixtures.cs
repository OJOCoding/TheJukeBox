using System.Buffers.Binary;
using System.Text;

namespace TheJukeBox.Tests;

/// <summary>
/// Builds minimal but genuinely valid audio files in memory.
/// </summary>
/// <remarks>
/// <para>
/// Hand-built rather than checked in as binaries, and hand-built rather than written by the
/// tag library itself. Committing binary fixtures to a repository makes them unreviewable
/// and unexplainable; asking the tag library to write them would test that it can read its
/// own output, which is the one thing never in doubt.
/// </para>
/// <para>
/// Constructing the containers by hand means the expected values are derived from the
/// specification rather than from the implementation. A duration is asserted because
/// STREAMINFO says the file contains exactly that many samples at that rate, not because
/// the reader said so.
/// </para>
/// </remarks>
internal static class AudioFixtures
{
    public const string Title = "Blue Calx";
    public const string Artist = "Aphex Twin";
    public const string Album = "Selected Ambient Works Volume II";
    public const int TrackNumber = 4;
    public const int Year = 1994;
    public const string Genre = "Ambient";

    public const int SampleRate = 44100;
    public const int DurationSeconds = 222;

    /// <summary>A tiny but structurally valid JPEG, used as embedded cover art.</summary>
    /// <remarks>
    /// Starts with the SOI marker and an APP0/JFIF header, so format sniffing recognises it,
    /// and ends with EOI. It decodes to nothing useful, which does not matter: nothing in
    /// the pipeline decodes cover art, it only hashes and stores it.
    /// </remarks>
    public static byte[] CoverJpeg { get; } =
    [
        0xFF, 0xD8,                                     // SOI
        0xFF, 0xE0, 0x00, 0x10,                         // APP0, length 16
        0x4A, 0x46, 0x49, 0x46, 0x00,                   // "JFIF\0"
        0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01,       // version, units, density
        0x00, 0x00,                                     // no thumbnail
        .. Enumerable.Repeat((byte)0x5A, 64),           // filler, so the bytes are distinctive
        0xFF, 0xD9,                                     // EOI
    ];

    // -----------------------------------------------------------------------
    // FLAC
    // -----------------------------------------------------------------------

    /// <summary>
    /// A FLAC file: signature, STREAMINFO, a Vorbis comment block, and optionally a picture.
    /// </summary>
    /// <remarks>
    /// No audio frames. Everything the reader needs is in the metadata blocks, and
    /// STREAMINFO carries the total sample count, so the duration is exact rather than
    /// estimated from a bitrate.
    /// </remarks>
    public static byte[] Flac(bool withPicture = false, int durationSeconds = DurationSeconds)
    {
        var blocks = new List<byte[]>
        {
            MetadataBlock(type: 0, StreamInfo(durationSeconds), isLast: false),
            MetadataBlock(type: 4, VorbisComment(), isLast: !withPicture),
        };

        if (withPicture)
        {
            blocks.Add(MetadataBlock(type: 6, PictureBlock(), isLast: true));
        }

        return [.. "fLaC"u8.ToArray(), .. blocks.SelectMany(b => b)];
    }

    private static byte[] MetadataBlock(byte type, byte[] body, bool isLast)
    {
        // One header byte: the top bit flags the final block, the low seven are the type.
        // Then a 24-bit big-endian length.
        var header = new byte[4];
        header[0] = (byte)((isLast ? 0x80 : 0x00) | type);
        header[1] = (byte)((body.Length >> 16) & 0xFF);
        header[2] = (byte)((body.Length >> 8) & 0xFF);
        header[3] = (byte)(body.Length & 0xFF);

        return [.. header, .. body];
    }

    private static byte[] StreamInfo(int durationSeconds)
    {
        var block = new byte[34];

        // Block sizes and frame sizes. Fixed 4096-sample blocks; frame sizes left unknown,
        // which is legal and signalled by zero.
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(0), 4096);
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(2), 4096);

        // The next four fields are not byte-aligned, so they are packed into one 64-bit
        // value: 20 bits sample rate, 3 bits (channels - 1), 5 bits (bit depth - 1),
        // 36 bits total samples. This is what makes the duration exact.
        ulong totalSamples = (ulong)SampleRate * (ulong)durationSeconds;

        var packed = ((ulong)SampleRate << 44)
                     | ((ulong)(2 - 1) << 41)
                     | ((ulong)(16 - 1) << 36)
                     | (totalSamples & 0xF_FFFF_FFFF);

        BinaryPrimitives.WriteUInt64BigEndian(block.AsSpan(10), packed);

        // Trailing 16 bytes are the MD5 of the unencoded audio; zero means "unknown".
        return block;
    }

    private static byte[] VorbisComment()
    {
        var comments = new[]
        {
            $"TITLE={Title}",
            $"ARTIST={Artist}",
            $"ALBUM={Album}",
            $"ALBUMARTIST={Artist}",
            $"TRACKNUMBER={TrackNumber}",
            $"DATE={Year}",
            $"GENRE={Genre}",
        };

        var body = new List<byte>();

        // Vorbis comments are little-endian, unlike every other length in FLAC.
        var vendor = "TheJukeBox test fixture"u8.ToArray();
        body.AddRange(LittleEndian(vendor.Length));
        body.AddRange(vendor);
        body.AddRange(LittleEndian(comments.Length));

        foreach (var comment in comments)
        {
            var bytes = Encoding.UTF8.GetBytes(comment);
            body.AddRange(LittleEndian(bytes.Length));
            body.AddRange(bytes);
        }

        return [.. body];
    }

    private static byte[] PictureBlock()
    {
        var mime = "image/jpeg"u8.ToArray();
        var description = "Cover"u8.ToArray();

        var body = new List<byte>();

        body.AddRange(BigEndian(3));               // picture type 3 = front cover
        body.AddRange(BigEndian(mime.Length));
        body.AddRange(mime);
        body.AddRange(BigEndian(description.Length));
        body.AddRange(description);
        body.AddRange(BigEndian(64));              // width
        body.AddRange(BigEndian(64));              // height
        body.AddRange(BigEndian(24));              // colour depth
        body.AddRange(BigEndian(0));               // indexed colours, 0 for non-indexed
        body.AddRange(BigEndian(CoverJpeg.Length));
        body.AddRange(CoverJpeg);

        return [.. body];
    }

    // -----------------------------------------------------------------------
    // MP3
    // -----------------------------------------------------------------------

    /// <summary>An MP3 carrying an ID3v2.3 tag at the front.</summary>
    public static byte[] Mp3WithId3v2(bool withPicture = false, int frames = 64)
    {
        var tag = Id3v2(withPicture);

        return [.. tag, .. MpegFrames(frames)];
    }

    /// <summary>
    /// An MP3 whose only tag is ID3v1, in the final 128 bytes.
    /// </summary>
    /// <remarks>
    /// The important fixture for the range stream. ID3v1 lives at the very end of the file,
    /// so reading it proves the reader can seek to the tail rather than only prefetching a
    /// head block. The same property is what makes a non-faststart M4A readable.
    /// </remarks>
    public static byte[] Mp3WithId3v1Only(int frames = 512)
    {
        return [.. MpegFrames(frames), .. Id3v1()];
    }

    /// <summary>A structurally valid MP3 with no tags at all.</summary>
    public static byte[] Mp3Untagged(int frames = 64) => MpegFrames(frames);

    private static byte[] Id3v2(bool withPicture)
    {
        var frames = new List<byte>();

        frames.AddRange(TextFrame("TIT2", Title));
        frames.AddRange(TextFrame("TPE1", Artist));
        frames.AddRange(TextFrame("TALB", Album));
        frames.AddRange(TextFrame("TPE2", Artist));
        frames.AddRange(TextFrame("TRCK", TrackNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        frames.AddRange(TextFrame("TYER", Year.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        frames.AddRange(TextFrame("TCON", Genre));

        if (withPicture)
        {
            frames.AddRange(PictureFrame());
        }

        var header = new List<byte>();
        header.AddRange("ID3"u8.ToArray());
        header.Add(3);                             // version 2.3
        header.Add(0);                             // revision
        header.Add(0);                             // flags
        header.AddRange(Synchsafe(frames.Count));  // size excludes the 10-byte header

        return [.. header, .. frames];
    }

    private static byte[] TextFrame(string id, string value)
    {
        // Encoding byte 0 means ISO-8859-1, which keeps the payload one byte per character
        // and avoids the BOM handling UTF-16 frames require.
        var payload = new List<byte> { 0x00 };
        payload.AddRange(Encoding.Latin1.GetBytes(value));

        return FrameHeader(id, payload.Count).Concat(payload).ToArray();
    }

    private static byte[] PictureFrame()
    {
        var payload = new List<byte> { 0x00 };                 // ISO-8859-1
        payload.AddRange("image/jpeg"u8.ToArray());
        payload.Add(0x00);                                     // MIME terminator
        payload.Add(0x03);                                     // front cover
        payload.Add(0x00);                                     // empty description, terminated
        payload.AddRange(CoverJpeg);

        return FrameHeader("APIC", payload.Count).Concat(payload).ToArray();
    }

    private static byte[] FrameHeader(string id, int size)
    {
        var header = new byte[10];
        Encoding.ASCII.GetBytes(id).CopyTo(header, 0);

        // ID3v2.3 frame sizes are plain big-endian, unlike the synchsafe tag size. Getting
        // this wrong is the classic ID3v2.3-versus-2.4 mistake.
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);

        return header;
    }

    private static byte[] Id3v1()
    {
        var tag = new byte[128];

        "TAG"u8.ToArray().CopyTo(tag, 0);
        WriteFixed(tag, 3, 30, Title);
        WriteFixed(tag, 33, 30, Artist);
        WriteFixed(tag, 63, 30, Album);
        WriteFixed(tag, 93, 4, Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteFixed(tag, 97, 30, string.Empty);
        tag[127] = 0;   // genre index 0 = Blues

        return tag;
    }

    private static void WriteFixed(byte[] target, int offset, int length, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        Array.Copy(bytes, 0, target, offset, Math.Min(bytes.Length, length));
    }

    /// <summary>
    /// A run of valid MPEG-1 Layer III frames.
    /// </summary>
    /// <remarks>
    /// Header <c>FF FB 90 00</c>: sync, MPEG-1, Layer III, no CRC, 128 kbps, 44.1 kHz, no
    /// padding, joint stereo. At that rate a frame is
    /// <c>144 * 128000 / 44100 = 417</c> bytes. The payload is zeroed, which decodes to
    /// silence and is irrelevant here; only the headers are parsed.
    /// </remarks>
    private static byte[] MpegFrames(int count)
    {
        const int frameSize = 417;

        var buffer = new byte[frameSize * count];

        for (var i = 0; i < count; i++)
        {
            var offset = i * frameSize;
            buffer[offset] = 0xFF;
            buffer[offset + 1] = 0xFB;
            buffer[offset + 2] = 0x90;
            buffer[offset + 3] = 0x00;
        }

        return buffer;
    }

    // -----------------------------------------------------------------------

    private static byte[] LittleEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>
    /// A synchsafe 32-bit integer: seven bits per byte, high bit always clear.
    /// </summary>
    /// <remarks>
    /// The point is that no byte can look like an MPEG sync word, so a decoder skipping the
    /// tag cannot mistake its length field for audio.
    /// </remarks>
    private static byte[] Synchsafe(int value) =>
    [
        (byte)((value >> 21) & 0x7F),
        (byte)((value >> 14) & 0x7F),
        (byte)((value >> 7) & 0x7F),
        (byte)(value & 0x7F),
    ];
}
