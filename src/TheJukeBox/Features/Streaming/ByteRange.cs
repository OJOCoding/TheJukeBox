using System.Globalization;

namespace TheJukeBox.Features.Streaming;

/// <summary>An inclusive byte range, as HTTP defines it.</summary>
public readonly record struct ByteRange(long From, long To)
{
    /// <summary>Number of bytes covered. Inclusive on both ends, so the +1 is real.</summary>
    public long Length => To - From + 1;

    /// <summary>The value for a <c>Content-Range</c> response header.</summary>
    public string ToContentRange(long totalLength) =>
        string.Create(CultureInfo.InvariantCulture, $"bytes {From}-{To}/{totalLength}");

    /// <summary>The value for a <c>Range</c> request header sent upstream.</summary>
    public string ToRangeHeader() =>
        string.Create(CultureInfo.InvariantCulture, $"bytes={From}-{To}");
}

/// <summary>What a client's <c>Range</c> header asked for.</summary>
public enum RangeOutcome
{
    /// <summary>No usable range: serve the whole entity as 200.</summary>
    Whole,

    /// <summary>A satisfiable range: serve 206 with <c>Content-Range</c>.</summary>
    Partial,

    /// <summary>Syntactically valid but outside the entity: 416.</summary>
    Unsatisfiable,
}

public readonly record struct RangeRequest(RangeOutcome Outcome, ByteRange Range)
{
    public static RangeRequest Whole(long totalLength) =>
        new(RangeOutcome.Whole, new ByteRange(0, Math.Max(totalLength - 1, 0)));

    public static RangeRequest Unsatisfiable => new(RangeOutcome.Unsatisfiable, default);

    public static RangeRequest Partial(long from, long to) => new(RangeOutcome.Partial, new ByteRange(from, to));
}

/// <summary>
/// Interprets a client's <c>Range</c> header.
/// </summary>
/// <remarks>
/// <para>
/// Written as a pure function and tested exhaustively before any network code exists,
/// because this is where every media-client quirk lives and none of it needs I/O to
/// reproduce. The framework's own range handling was not reusable here: it slices a
/// stream it can already seek, whereas this decides what to ask Drive for.
/// </para>
/// <para>
/// The governing rule from RFC 9110 is that an unparseable or unsupported
/// <c>Range</c> must be <em>ignored</em> and the whole entity returned, never rejected.
/// Returning 400 or 416 for a header shape we simply do not implement is what breaks
/// media elements, because they retry the same header and give up.
/// </para>
/// </remarks>
public static class RangeResolver
{
    private const string BytesUnit = "bytes=";

    /// <summary>
    /// Resolves <paramref name="headerValue"/> against an entity of
    /// <paramref name="totalLength"/> bytes.
    /// </summary>
    public static RangeRequest Resolve(string? headerValue, long totalLength)
    {
        if (totalLength <= 0)
        {
            // Nothing can be partial about an empty entity, and 416 here would make a
            // zero-byte file unplayable rather than merely silent.
            return RangeRequest.Whole(totalLength);
        }

        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return RangeRequest.Whole(totalLength);
        }

        var value = headerValue.Trim();

        // Only the bytes unit exists in practice. Any other unit must be ignored.
        if (!value.StartsWith(BytesUnit, StringComparison.OrdinalIgnoreCase))
        {
            return RangeRequest.Whole(totalLength);
        }

        var spec = value[BytesUnit.Length..].Trim();

        if (spec.Length == 0)
        {
            return RangeRequest.Whole(totalLength);
        }

        // Multiple ranges are legal and essentially unused by media clients. Serving a
        // multipart/byteranges response for them is a lot of machinery; honouring only
        // the first is a valid, and far simpler, choice. Ignoring the header entirely
        // would be worse, because the client would receive the whole file.
        var firstComma = spec.IndexOf(',', StringComparison.Ordinal);

        if (firstComma >= 0)
        {
            spec = spec[..firstComma].Trim();
        }

        var dash = spec.IndexOf('-', StringComparison.Ordinal);

        if (dash < 0)
        {
            return RangeRequest.Whole(totalLength);
        }

        var fromText = spec[..dash].Trim();
        var toText = spec[(dash + 1)..].Trim();

        // Suffix form, "bytes=-500": the last 500 bytes.
        if (fromText.Length == 0)
        {
            if (!TryParse(toText, out var suffixLength) || suffixLength <= 0)
            {
                return RangeRequest.Whole(totalLength);
            }

            var from = Math.Max(totalLength - suffixLength, 0);

            return RangeRequest.Partial(from, totalLength - 1);
        }

        if (!TryParse(fromText, out var start))
        {
            return RangeRequest.Whole(totalLength);
        }

        // A start at or past the end is the one case that genuinely is 416. Everything
        // else that looks odd gets the whole entity.
        if (start >= totalLength)
        {
            return RangeRequest.Unsatisfiable;
        }

        // Open-ended, "bytes=0-", which is how Chrome and Firefox open a media element.
        if (toText.Length == 0)
        {
            return RangeRequest.Partial(start, totalLength - 1);
        }

        if (!TryParse(toText, out var end))
        {
            // Covers "bytes=0-*", which some clients emit for a blob URL. Not valid
            // HTTP, and answering 416 makes the media fail to load, so treat the header
            // as absent.
            return RangeRequest.Whole(totalLength);
        }

        if (end < start)
        {
            return RangeRequest.Whole(totalLength);
        }

        // Clamped rather than rejected: asking beyond the end is explicitly allowed, and
        // clients routinely request a fixed window near EOF.
        return RangeRequest.Partial(start, Math.Min(end, totalLength - 1));
    }

    private static bool TryParse(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
