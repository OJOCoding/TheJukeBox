namespace TheJukeBox.Infrastructure.Metadata;

/// <summary>
/// Raised when a reader asks for more of a file than the metadata budget allows.
/// </summary>
/// <remarks>
/// A distinct type, and deliberately not a <see cref="Errors.JukeBoxException"/>: this is
/// internal flow control, not something a client ever sees. The enricher catches it and
/// falls back to filename parsing, which is what turns "one pathological file hangs the
/// sync" into "one track has a guessed title".
/// </remarks>
internal sealed class RangeBudgetExceededException(string message) : Exception(message);

/// <summary>
/// A read-only, seekable <see cref="Stream"/> over a remote file, faulting in fixed-size
/// blocks through ranged requests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a lazy seekable stream rather than "fetch the first N bytes and hope".</b>
/// ID3v2 and FLAC/Vorbis metadata blocks do sit at the front of a file, so a head fetch
/// appears to work right up until it meets real music. But <b>ID3v1 is the last 128 bytes
/// of the file</b>, and an <b>M4A/MP4 written without faststart keeps its <c>moov</c>
/// metadata atom at the end</b>, after however many megabytes of <c>mdat</c>. A fixed
/// head fetch returns nothing for either; a fixed head plus tail fetch still fails on the
/// interleaved MP4 that wants a table from the middle. A seekable stream needs no special
/// case at all: it faults in whichever block the reader actually asks for, and the tag
/// library's own format knowledge decides where that is.
/// </para>
/// <para>
/// <b>Cost.</b> Blocks are 256 KiB and at most eight are held, so one stream is bounded at
/// 2 MiB of managed memory and, by the budget below, at 6 MiB transferred. For a
/// 5,000-track library that is roughly 1.9 GB of ranged reads against about 40 GB if
/// whole files were downloaded, for identical information. That ratio is the entire
/// justification for this class.
/// </para>
/// <para>
/// <b>The budget is load-bearing.</b> A tag reader handed a malformed or unusually
/// interleaved MP4 can walk the file linearly, and without a cap a single such file
/// issues hundreds of requests and stalls a sync behind one track. Exceeding either the
/// request count or the byte ceiling throws <see cref="RangeBudgetExceededException"/> so
/// the caller degrades to filename parsing instead of hanging.
/// </para>
/// <para>
/// <b>Length is supplied, never probed.</b> Drive already reported the size during
/// enumeration and it is on the <c>Track</c> row, so this class costs zero metadata
/// requests. Below <see cref="WholeFileThreshold"/> the file is fetched in a single
/// request, because ranging a file smaller than a few blocks is pure round-trip overhead.
/// </para>
/// </remarks>
internal sealed class DriveRandomAccessStream : Stream
{
    /// <summary>Block granularity. Large enough that an ID3v2 tag with cover art is usually one block.</summary>
    internal const int BlockSize = 256 * 1024;

    /// <summary>Blocks held at once, bounding one stream at 2 MiB of managed memory.</summary>
    internal const int MaxCachedBlocks = 8;

    /// <summary>Hard ceiling on requests per file. See the budget note above.</summary>
    internal const int MaxRequests = 12;

    /// <summary>Hard ceiling on bytes transferred per file.</summary>
    internal const long MaxBytesFetched = 6L * 1024 * 1024;

    /// <summary>At or below this size the whole file is fetched in one request.</summary>
    internal const long WholeFileThreshold = 1024 * 1024;

    private readonly IRangeReader _reader;
    private readonly CancellationToken _cancellationToken;

    /// <summary>
    /// Resident blocks, least-recently-used first. A list rather than a dictionary plus an
    /// intrusive list: with a ceiling of eight entries a linear scan is faster than
    /// hashing, and the eviction order is visible in the data structure.
    /// </summary>
    private readonly List<Block> _blocks = new(MaxCachedBlocks);

    private readonly bool _wholeFile;

    private long _position;
    private bool _disposed;

    private DriveRandomAccessStream(IRangeReader reader, long length, CancellationToken cancellationToken)
    {
        _reader = reader;
        Length = length;
        _cancellationToken = cancellationToken;
        _wholeFile = length <= WholeFileThreshold;
    }

    /// <summary>Requests issued so far, for assertions and logging.</summary>
    public int RequestCount { get; private set; }

    /// <summary>Bytes transferred so far, for assertions and logging.</summary>
    public long BytesFetched { get; private set; }

    public override bool CanRead => true;

    /// <summary>
    /// Always true, and the reason this class exists. A tag reader given a forward-only
    /// stream either buffers the whole file or gives up.
    /// </summary>
    public override bool CanSeek => true;

    public override bool CanWrite => false;

    /// <summary>The size Drive already reported. No HEAD request is ever issued.</summary>
    public override long Length { get; }

    public override long Position
    {
        get => _position;

        // Seeking past the end is legal and does not fetch anything; the following Read
        // simply returns 0. Media parsers routinely seek speculatively.
        set => _position = value < 0
            ? throw new ArgumentOutOfRangeException(nameof(value), "A stream position cannot be negative.")
            : value;
    }

    /// <summary>
    /// Opens the stream and fetches the first block up front.
    /// </summary>
    /// <remarks>
    /// Block zero is prefetched asynchronously here so the one fetch every format needs
    /// (the magic bytes, and for most formats the whole tag) happens without blocking a
    /// thread. Later faults are synchronous because <see cref="Stream.Read(Span{byte})"/>
    /// is, and every tag library reads synchronously.
    /// </remarks>
    public static async Task<DriveRandomAccessStream> OpenAsync(
        IRangeReader reader,
        long length,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var stream = new DriveRandomAccessStream(reader, length, cancellationToken);

        if (length > 0)
        {
            await stream.FetchAsync(0, cancellationToken).ConfigureAwait(false);
        }

        return stream;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> completely unless the end of the file is reached.
    /// </summary>
    /// <remarks>
    /// A short read is legal for a <see cref="Stream"/>, but plenty of parsing code (and
    /// <see cref="BinaryReader"/> in some paths) assumes a single call returns everything
    /// asked for. Looping across block boundaries here removes a whole class of
    /// format-dependent bug at no cost, because a read spanning two blocks would have had
    /// to fault both in anyway.
    /// </remarks>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var written = 0;

        while (written < buffer.Length && _position < Length)
        {
            var block = BlockFor(_position);
            var inBlock = (int)(_position - block.Offset);
            var available = block.Length - inBlock;

            if (available <= 0)
            {
                break;
            }

            var take = Math.Min(available, buffer.Length - written);

            block.Data.AsSpan(inBlock, take).CopyTo(buffer[written..]);

            _position += take;
            written += take;
        }

        return written;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];

        return Read(one) == 1 ? one[0] : -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
        {
            throw new IOException("Cannot seek before the start of the stream.");
        }

        _position = target;

        return _position;
    }

    public override void Flush()
    {
        // Nothing is buffered for writing. Not throwing, because callers flush defensively.
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException("The stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _blocks.Clear();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// The resident block containing <paramref name="position"/>, faulting it in if needed.
    /// </summary>
    private Block BlockFor(long position)
    {
        var offset = _wholeFile ? 0 : position / BlockSize * BlockSize;

        for (var i = 0; i < _blocks.Count; i++)
        {
            var candidate = _blocks[i];

            if (candidate.Offset != offset)
            {
                continue;
            }

            // Touched: move to the back so eviction takes the genuinely coldest block.
            // Tag readers revisit the header repeatedly while walking a file, so keeping
            // block zero resident is what stops it being refetched and burning budget.
            if (i != _blocks.Count - 1)
            {
                _blocks.RemoveAt(i);
                _blocks.Add(candidate);
            }

            return candidate;
        }

        // Synchronous by necessity: Stream.Read is synchronous and so is every tag
        // library. Safe from the classic deadlock because ASP.NET Core installs no
        // SynchronizationContext, so the continuation runs on the thread pool rather than
        // waiting for the thread this call is blocking. Extraction runs on a background
        // worker, never on a request thread.
        return FetchAsync(offset, _cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private async Task<Block> FetchAsync(long offset, CancellationToken cancellationToken)
    {
        var length = (int)Math.Min(_wholeFile ? Length : BlockSize, Length - offset);

        if (length <= 0)
        {
            return new Block(offset, [], 0);
        }

        // Checked before the request, not after, so the ceilings are ceilings rather than
        // thresholds that get overshot by one block.
        if (RequestCount + 1 > MaxRequests || BytesFetched + length > MaxBytesFetched)
        {
            throw new RangeBudgetExceededException(
                $"Reading metadata exceeded the budget of {MaxRequests} range requests / "
                + $"{MaxBytesFetched} bytes after {RequestCount} request(s) and {BytesFetched} byte(s).");
        }

        var data = new byte[length];

        RequestCount++;

        var read = await _reader.ReadRangeAsync(offset, data, cancellationToken).ConfigureAwait(false);

        BytesFetched += read;

        var block = new Block(offset, data, read);

        if (_blocks.Count >= MaxCachedBlocks)
        {
            _blocks.RemoveAt(0);
        }

        _blocks.Add(block);

        return block;
    }

    /// <summary>One resident window of the file.</summary>
    /// <param name="Length">
    /// May be shorter than <paramref name="Data"/> at the end of the file, or if the
    /// server returned a short range.
    /// </param>
    private sealed record Block(long Offset, byte[] Data, int Length);
}
