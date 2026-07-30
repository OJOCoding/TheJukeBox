using TheJukeBox.Infrastructure.Metadata;

namespace TheJukeBox.Tests;

/// <summary>
/// An <see cref="IRangeReader"/> over an in-memory array that counts its calls.
/// </summary>
/// <remarks>
/// The counting is the point. Field correctness can be asserted many ways, but only a
/// request count catches the pipeline quietly regressing to whole-file downloads, which is
/// the entire reason this stream exists: reading tags by pulling complete files would cost
/// roughly 40 GB of Drive egress for a 5,000-track library instead of about 1.9 GB, for
/// identical information.
/// </remarks>
internal sealed class CountingRangeReader(byte[] content) : IRangeReader
{
    public int Requests { get; private set; }

    public long BytesServed { get; private set; }

    public List<(long Offset, int Length)> Windows { get; } = [];

    public Task<int> ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        Requests++;

        if (offset >= content.Length)
        {
            Windows.Add((offset, 0));
            return Task.FromResult(0);
        }

        var available = (int)Math.Min(destination.Length, content.Length - offset);
        content.AsSpan((int)offset, available).CopyTo(destination.Span);

        BytesServed += available;
        Windows.Add((offset, available));

        return Task.FromResult(available);
    }
}

public class DriveRandomAccessStreamTests
{
    /// <summary>Deterministic content, so a wrong offset produces a wrong value.</summary>
    private static byte[] Content(int length)
    {
        var bytes = new byte[length];

        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private static async Task<(DriveRandomAccessStream Stream, CountingRangeReader Reader)> OpenAsync(int length)
    {
        var content = Content(length);
        var reader = new CountingRangeReader(content);

        return (await DriveRandomAccessStream.OpenAsync(reader, length), reader);
    }

    [Fact]
    public async Task Reports_the_length_it_was_told_without_asking()
    {
        var (stream, reader) = await OpenAsync(5 * DriveRandomAccessStream.BlockSize);

        // Length comes from the already-known file size, so no HEAD or metadata call is
        // needed. The single request is the eager first block.
        Assert.Equal(5 * DriveRandomAccessStream.BlockSize, stream.Length);
        Assert.Equal(1, reader.Requests);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public async Task Reads_the_opening_bytes_from_the_prefetched_block()
    {
        var (stream, reader) = await OpenAsync(4 * DriveRandomAccessStream.BlockSize);

        var buffer = new byte[64];
        Assert.Equal(64, stream.Read(buffer, 0, 64));

        // ID3v2 and FLAC/Vorbis metadata live at the front, so the overwhelmingly common
        // case must cost exactly one request.
        Assert.Equal(1, reader.Requests);
        Assert.Equal(Content(64), buffer);
    }

    /// <summary>
    /// The case that makes a lazy seekable stream necessary rather than merely tidy: an
    /// M4A written without faststart keeps its moov metadata atom at the end of the file,
    /// and ID3v1 is the last 128 bytes. A fixed head-only prefetch cannot read either.
    /// </summary>
    [Fact]
    public async Task Reads_the_final_bytes_without_fetching_the_whole_file()
    {
        const int length = 40 * DriveRandomAccessStream.BlockSize;
        var (stream, reader) = await OpenAsync(length);

        stream.Seek(-128, SeekOrigin.End);

        var tail = new byte[128];
        Assert.Equal(128, stream.Read(tail, 0, 128));

        Assert.Equal(Content(length).AsSpan(length - 128).ToArray(), tail);

        // Two blocks: the eager first one, and the one containing the tail. Emphatically
        // not the 40 a whole-file read would need.
        Assert.Equal(2, reader.Requests);
        Assert.True(
            stream.BytesFetched <= 2 * DriveRandomAccessStream.BlockSize,
            $"fetched {stream.BytesFetched} bytes for a 128-byte tail read");
    }

    [Fact]
    public async Task Reads_across_a_block_boundary()
    {
        var (stream, _) = await OpenAsync(3 * DriveRandomAccessStream.BlockSize);

        // Straddles the first boundary, so a stream that silently truncated at a block
        // edge would return short and corrupt whatever parsed it.
        stream.Seek(DriveRandomAccessStream.BlockSize - 8, SeekOrigin.Begin);

        var buffer = new byte[16];
        var total = 0;

        while (total < 16)
        {
            var read = stream.Read(buffer, total, 16 - total);
            Assert.True(read > 0, "the stream stopped early inside the entity");
            total += read;
        }

        var expected = Content(3 * DriveRandomAccessStream.BlockSize)
            .AsSpan(DriveRandomAccessStream.BlockSize - 8, 16)
            .ToArray();

        Assert.Equal(expected, buffer);
    }

    [Fact]
    public async Task Serves_a_repeated_read_of_the_same_block_from_cache()
    {
        var (stream, reader) = await OpenAsync(10 * DriveRandomAccessStream.BlockSize);

        var buffer = new byte[32];

        for (var i = 0; i < 20; i++)
        {
            stream.Seek(i % 4, SeekOrigin.Begin);

            // Asserted rather than discarded: a read that silently returned 0 would make
            // the request count trivially 1 and the test meaningless.
            Assert.Equal(32, stream.Read(buffer, 0, 32));
        }

        // Tag readers issue many small reads. Without caching, each would be a request and
        // the budget would be exhausted immediately.
        Assert.Equal(1, reader.Requests);
    }

    [Fact]
    public async Task Small_files_are_fetched_whole_because_ranging_is_pure_overhead()
    {
        var length = (int)DriveRandomAccessStream.WholeFileThreshold / 2;
        var (stream, reader) = await OpenAsync(length);

        stream.Seek(length - 16, SeekOrigin.Begin);

        var buffer = new byte[16];
        Assert.Equal(16, stream.Read(buffer, 0, 16));

        // One request covered the entire file, so even a tail read costs nothing extra.
        Assert.Equal(1, reader.Requests);
    }

    [Fact]
    public async Task Reading_past_the_end_returns_zero_rather_than_throwing()
    {
        var (stream, _) = await OpenAsync(1024);

        stream.Seek(0, SeekOrigin.End);

        Assert.Equal(0, stream.Read(new byte[16], 0, 16));
        Assert.Equal(-1, stream.ReadByte());
    }

    /// <summary>
    /// The budget is what stops one pathological file from issuing hundreds of requests.
    /// A malformed MP4 whose parser scans linearly is the realistic cause, and without a
    /// cap it would stall a whole sync pass.
    /// </summary>
    [Fact]
    public async Task Exceeding_the_request_budget_throws_rather_than_grinding_on()
    {
        // Far more blocks than the cache holds, walked in a pattern that defeats it.
        var length = (DriveRandomAccessStream.MaxRequests + 20) * DriveRandomAccessStream.BlockSize;
        var (stream, reader) = await OpenAsync(length);

        var buffer = new byte[8];

        var thrown = Assert.Throws<RangeBudgetExceededException>(() =>
        {
            for (var block = 0; block < DriveRandomAccessStream.MaxRequests + 20; block++)
            {
                stream.Seek((long)block * DriveRandomAccessStream.BlockSize, SeekOrigin.Begin);

                var read = stream.Read(buffer, 0, 8);
                Assert.True(read > 0, "the stream returned no bytes inside the entity");
            }
        });

        Assert.NotNull(thrown.Message);

        // Bounded, and bounded at the documented figure rather than merely "eventually".
        Assert.True(
            reader.Requests <= DriveRandomAccessStream.MaxRequests + 1,
            $"issued {reader.Requests} requests against a budget of {DriveRandomAccessStream.MaxRequests}");

        Assert.True(
            stream.BytesFetched <= DriveRandomAccessStream.MaxBytesFetched + DriveRandomAccessStream.BlockSize,
            $"fetched {stream.BytesFetched} bytes against a budget of {DriveRandomAccessStream.MaxBytesFetched}");
    }

    [Fact]
    public async Task An_empty_file_needs_no_request_at_all()
    {
        var (stream, reader) = await OpenAsync(0);

        Assert.Equal(0, stream.Length);
        Assert.Equal(0, stream.Read(new byte[8], 0, 8));
        Assert.Equal(0, reader.Requests);
    }

    [Theory]
    [InlineData(SeekOrigin.Begin, 100, 100)]
    [InlineData(SeekOrigin.Current, 50, 50)]
    [InlineData(SeekOrigin.End, -100, 924)]
    public async Task Seek_resolves_every_origin(SeekOrigin origin, long offset, long expected)
    {
        var (stream, _) = await OpenAsync(1024);

        Assert.Equal(expected, stream.Seek(offset, origin));
        Assert.Equal(expected, stream.Position);
    }

    [Fact]
    public async Task Writing_is_refused()
    {
        var (stream, _) = await OpenAsync(1024);

        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[8], 0, 8));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(4096));
    }
}
