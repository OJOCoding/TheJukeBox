using TheJukeBox.Features.Streaming;

namespace TheJukeBox.Tests;

/// <summary>
/// The browser-behaviour table. Every row is a request shape a real media client sends,
/// or a malformed one that must not be answered with an error.
/// </summary>
public class RangeResolverTests
{
    private const long Length = 1000;

    [Theory]
    // No header at all: the very first request from any client.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Units other than bytes must be ignored, not rejected.
    [InlineData("items=0-10")]
    [InlineData("bytes=")]
    // Unparseable specs. RFC 9110 requires ignoring these and returning the whole
    // entity; answering 400 or 416 makes a media element retry and then give up.
    [InlineData("bytes=abc")]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=10")]
    [InlineData("bytes=-")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=100-50")]
    // "bytes=0-*" is emitted by some clients for blob URLs. Not valid HTTP, and a 416
    // here is what makes offline playback fail rather than merely degrade.
    [InlineData("bytes=0-*")]
    [InlineData("bytes=0-abc")]
    // Negative and overflowing numbers must not parse as values.
    [InlineData("bytes=-5-10")]
    [InlineData("bytes=99999999999999999999-")]
    // RFC 9110's grammar is "range-unit = range-set" with no whitespace permitted
    // around the equals sign, so this is malformed. No real client sends it, and the
    // safe response to a header we cannot parse is the whole entity.
    [InlineData("bytes = 0-99")]
    public void Unusable_ranges_serve_the_whole_entity(string? header)
    {
        var resolved = RangeResolver.Resolve(header, Length);

        Assert.Equal(RangeOutcome.Whole, resolved.Outcome);
    }

    [Theory]
    // Chrome and Firefox open a media element with this.
    [InlineData("bytes=0-", 0, 999)]
    // Safari opens with a two-byte probe, then asks for the rest. Getting this wrong is
    // the single most common cause of "works everywhere except iOS".
    [InlineData("bytes=0-1", 0, 1)]
    [InlineData("bytes=2-", 2, 999)]
    // A mid-file seek.
    [InlineData("bytes=500-", 500, 999)]
    [InlineData("bytes=500-600", 500, 600)]
    // Exactly the last byte.
    [InlineData("bytes=999-", 999, 999)]
    [InlineData("bytes=999-999", 999, 999)]
    // Asking past the end is explicitly allowed and must be clamped, not rejected:
    // clients routinely request a fixed window near EOF.
    [InlineData("bytes=900-99999", 900, 999)]
    [InlineData("bytes=0-99999", 0, 999)]
    // Suffix form: the last N bytes. Used to read trailing metadata such as ID3v1.
    [InlineData("bytes=-128", 872, 999)]
    [InlineData("bytes=-1", 999, 999)]
    // A suffix longer than the file is the whole file, not an error.
    [InlineData("bytes=-99999", 0, 999)]
    // Surrounding whitespace is trimmed, and the range unit is case-insensitive.
    [InlineData(" bytes=0-99 ", 0, 99)]
    [InlineData("bytes=0 - 99", 0, 99)]
    [InlineData("BYTES=0-99", 0, 99)]
    // Multiple ranges: honour the first rather than building a multipart response.
    [InlineData("bytes=0-99,200-299", 0, 99)]
    public void Satisfiable_ranges_resolve_to_inclusive_bounds(string header, long expectedFrom, long expectedTo)
    {
        var resolved = RangeResolver.Resolve(header, Length);

        Assert.Equal(RangeOutcome.Partial, resolved.Outcome);
        Assert.Equal(expectedFrom, resolved.Range.From);
        Assert.Equal(expectedTo, resolved.Range.To);
    }

    /// <summary>
    /// The only genuinely unsatisfiable case: a start at or beyond the end. Every other
    /// odd input gets the whole entity.
    /// </summary>
    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=1000-1100")]
    [InlineData("bytes=5000-")]
    public void A_start_past_the_end_is_unsatisfiable(string header) =>
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeResolver.Resolve(header, Length).Outcome);

    /// <summary>
    /// A zero-byte entity must not produce 416, which would make an empty file report as
    /// broken rather than simply silent.
    /// </summary>
    [Theory]
    [InlineData("bytes=0-")]
    [InlineData("bytes=0-100")]
    [InlineData(null)]
    public void An_empty_entity_is_always_whole(string? header) =>
        Assert.Equal(RangeOutcome.Whole, RangeResolver.Resolve(header, 0).Outcome);

    [Fact]
    public void Length_counts_both_ends()
    {
        // 0-0 is one byte, not zero. Getting this off by one corrupts every response.
        Assert.Equal(1, new ByteRange(0, 0).Length);
        Assert.Equal(2, new ByteRange(0, 1).Length);
        Assert.Equal(1000, new ByteRange(0, 999).Length);
        Assert.Equal(100, new ByteRange(900, 999).Length);
    }

    [Fact]
    public void Header_values_are_formatted_as_HTTP_requires()
    {
        var range = new ByteRange(500, 999);

        Assert.Equal("bytes 500-999/1000", range.ToContentRange(1000));
        Assert.Equal("bytes=500-999", range.ToRangeHeader());
    }

    /// <summary>
    /// Content-Range must always be relative to the full entity, never to the slice, or
    /// the client cannot work out the duration and the seek bar breaks.
    /// </summary>
    [Fact]
    public void Content_range_reports_the_total_not_the_slice_length() =>
        Assert.EndsWith("/5000", new ByteRange(100, 199).ToContentRange(5000), StringComparison.Ordinal);

    /// <summary>
    /// Whatever a client asks for, the resolved range must lie inside the entity. This is
    /// the invariant the streaming endpoint depends on to avoid asking Drive for bytes
    /// that do not exist.
    /// </summary>
    [Theory]
    [InlineData("bytes=0-")]
    [InlineData("bytes=0-1")]
    [InlineData("bytes=-128")]
    [InlineData("bytes=900-99999")]
    [InlineData("bytes=0-99,200-299")]
    [InlineData("bytes=999-")]
    public void Resolved_ranges_always_fall_within_the_entity(string header)
    {
        var resolved = RangeResolver.Resolve(header, Length);

        Assert.Equal(RangeOutcome.Partial, resolved.Outcome);
        Assert.InRange(resolved.Range.From, 0, Length - 1);
        Assert.InRange(resolved.Range.To, resolved.Range.From, Length - 1);
    }
}
