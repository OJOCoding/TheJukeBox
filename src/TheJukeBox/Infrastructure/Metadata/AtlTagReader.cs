using ATL;
using TheJukeBox.Diagnostics;

namespace TheJukeBox.Infrastructure.Metadata;

/// <summary>
/// An <see cref="ITagReader"/> over ATL.NET (<c>z440.atl.core</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why ATL rather than TagLibSharp.</b> Two things decided it, in this order.
/// </para>
/// <para>
/// First, <b>licence</b>. ATL is MIT; TagLibSharp is LGPL-2.1. This repository is public
/// and MIT, and self-hosters are expected to fork and modify it. LGPL is perfectly usable
/// via dynamic linking, but it puts a relinking obligation on anyone who vendors or
/// trims the assembly, and explaining that in a self-hosting guide is a real cost for a
/// hobby music server.
/// </para>
/// <para>
/// Second, and decisively, <b>ATL reads an arbitrary seekable stream directly</b>:
/// <c>new Track(Stream, mimeType)</c> is a first-class constructor, and with an empty MIME
/// type it sniffs the format from the stream's own header. TagLibSharp can also be fed a
/// stream, but only through an <c>IFileAbstraction</c> shim whose <c>ReadStream</c> must be
/// both readable and seekable and whose <c>WriteStream</c>/<c>CloseStream</c> members have
/// to be stubbed out. ATL needed no adapter at all, so the pipeline in
/// <see cref="Features.Library.MetadataEnricher"/> hands
/// <see cref="DriveRandomAccessStream"/> straight to it. Nothing was descoped to make that
/// work, so the licence advantage came free.
/// </para>
/// <para>
/// <b>Duration and the cost of getting it.</b> Most tag libraries offer a "tags only, skip
/// audio properties" mode, and we cannot use it, because duration is exactly what the
/// transport needs and it is an audio property rather than a tag. ATL's compromise is the
/// right one for a ranged stream: with <c>Settings.MP3_parseExactDuration</c> left at its
/// default of <see langword="false"/> it derives an MP3's duration from the first frame
/// header plus any Xing/VBRI header, which is one block at the front of the file. Turning
/// it on would walk every frame in the file, which for a 10 MB MP3 means reading all of it
/// and blowing the range budget on every single track, to correct a duration by a second or
/// two on badly written VBR files. Container formats (FLAC, MP4, Ogg) state duration in
/// their header outright, so they cost nothing either way.
/// </para>
/// </remarks>
internal sealed class AtlTagReader(ILogger<AtlTagReader> logger) : ITagReader
{
    /// <summary>
    /// Pictures larger than this are dropped rather than stored.
    /// </summary>
    /// <remarks>
    /// A 12 MB cover is a scan somebody forgot to resize, not artwork, and storing it would
    /// dominate the database. Note the honest limit of this cap: ATL has already
    /// materialised the picture into a byte array by the time we can measure it, so this
    /// bounds what is hashed, transferred to SQLite, and served, not what was read off the
    /// wire. The range budget is what bounds the read.
    /// </remarks>
    internal const int MaxArtBytes = 12 * 1024 * 1024;

    static AtlTagReader()
    {
        // ATL's settings are process-wide statics, so they are set once here rather than
        // per read, where concurrent extractions would race on them.

        // A server must never write to the console on a per-file basis. ATL's default
        // dumps stack traces there for every malformed file it meets, and a library
        // assembled from the internet contains plenty.
        Settings.OutputStacktracesToConsole = false;

        // Absent fields come back as null rather than "", so "the tag said nothing" is
        // distinguishable from "the tag said empty" without string gymnastics. That
        // distinction is what decides whether the filename guess still wins.
        Settings.NullAbsentValues = true;

        // Unknown and non-standard frames are collected into AdditionalFields, which this
        // application never reads. Skipping them avoids allocating a dictionary of junk
        // per track across thousands of tracks.
        Settings.ReadAllMetaFrames = false;

        // There is no file path behind these streams, so ATL's "use the file name when
        // there is no title" convenience would invent a title from nothing. The filename
        // fallback is the parser's job and it is much better at it.
        Settings.UseFileNameWhenNoTitle = false;
    }

    public bool TryRead(Stream seekable, string fileName, string? mimeType, out TrackTags tags)
    {
        ArgumentNullException.ThrowIfNull(seekable);

        tags = new TrackTags();

        if (!seekable.CanSeek)
        {
            // Refused rather than buffered. Buffering to make a forward-only stream
            // seekable is precisely the whole-file download this design exists to avoid,
            // and doing it silently would hide the regression.
            throw new ArgumentException("A tag reader requires a seekable stream.", nameof(seekable));
        }

        try
        {
            seekable.Position = 0;

            // An empty MIME type makes ATL sniff the format from the stream header, which
            // is more reliable than Drive's guess. A hint is only supplied when we have a
            // usable one, and a wrong hint is worse than none.
            var hint = NormaliseHint(mimeType, fileName);

            var track = new Track(seekable, hint);

            // ID 0 is ATL's "Unknown": the stream was read but no format claimed it.
            // Distinguished from an exception because this is the ordinary outcome for a
            // file that is not really audio, and it must not be retried forever.
            if (track.AudioFormat is null || track.AudioFormat.ID <= 0)
            {
                Log.TagFormatUnrecognised(logger, fileName, hint);

                return false;
            }

            tags = Project(track);

            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException and not RangeBudgetExceededException)
        {
            // Anything a third-party parser throws on a malformed file. Deliberately
            // broad: the contract of this method is "never let one bad file matter", and
            // enumerating the exception types a tag library can raise on arbitrary bytes
            // is not a winnable game. The budget exception is let through on purpose, so
            // the caller can tell "too expensive" from "unparsable".
            Log.TagReadFailed(logger, fileName, e);

            return false;
        }
    }

    /// <summary>
    /// Picks the MIME hint to give ATL, preferring nothing over a wrong answer.
    /// </summary>
    private static string NormaliseHint(string? mimeType, string fileName)
    {
        // Drive reports application/octet-stream for plenty of audio, and octet-stream
        // matches no ATL format, so passing it through would fail every such file.
        if (mimeType is { Length: > 0 } && mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return mimeType;
        }

        var extension = AudioFormats.ResolveExtension(fileName, mimeType);
        var resolved = AudioFormats.ContentTypeFor(extension);

        return resolved.StartsWith("audio/", StringComparison.Ordinal) ? resolved : string.Empty;
    }

    private static TrackTags Project(Track track) => new()
    {
        Title = Trimmed(track.Title),
        Artist = Trimmed(track.Artist),
        Album = Trimmed(track.Album),
        AlbumArtist = Trimmed(track.AlbumArtist),
        TrackNumber = Positive(track.TrackNumber),
        DiscNumber = Positive(track.DiscNumber),

        // ATL exposes Year separately from a full Date; either can be the only one
        // populated depending on the tag version, and a bare year is what we store.
        Year = Plausible(track.Year ?? track.Date?.Year),
        Genre = Trimmed(track.Genre),

        // Rounded from milliseconds rather than reading the int Duration property, so a
        // 3.6 second file is 4 seconds and not 3.
        DurationSeconds = track.DurationMs > 0 ? (int)Math.Round(track.DurationMs / 1000d) : null,
        Art = SelectArt(track),
        Formats = track.MetadataFormats.Count == 0
            ? null
            : string.Join(", ", track.MetadataFormats.Select(f => f.ShortName)),
    };

    /// <summary>
    /// Chooses one picture out of however many the file carries.
    /// </summary>
    /// <remarks>
    /// Files routinely embed a front cover, a back cover, a CD face, and a band photo. The
    /// front cover is the only one a transport should show, so it is preferred explicitly
    /// rather than taking whichever happened to be written first.
    /// </remarks>
    private static EmbeddedArt? SelectArt(Track track)
    {
        if (track.EmbeddedPictures.Count == 0)
        {
            return null;
        }

        var picture = track.EmbeddedPictures.FirstOrDefault(p => p.PicType == PictureInfo.PIC_TYPE.Front)
                      ?? track.EmbeddedPictures.FirstOrDefault(p => p.PicType == PictureInfo.PIC_TYPE.Generic)
                      ?? track.EmbeddedPictures[0];

        var bytes = picture.PictureData;

        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxArtBytes)
        {
            return null;
        }

        return new EmbeddedArt(bytes, Trimmed(picture.MimeType));
    }

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;

    /// <summary>
    /// Rejects years that cannot be right.
    /// </summary>
    /// <remarks>
    /// Tags carry 0, 1, and 1900 as "unset" often enough that storing them unfiltered
    /// makes a decade-sorted view useless. The lower bound is the first commercial
    /// recordings; the upper bound allows for a release dated slightly ahead.
    /// </remarks>
    private static int? Plausible(int? year) => year is >= 1900 and <= 2200 ? year : null;
}
