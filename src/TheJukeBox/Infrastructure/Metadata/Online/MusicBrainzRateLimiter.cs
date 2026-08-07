using System.Diagnostics;

namespace TheJukeBox.Infrastructure.Metadata.Online;

/// <summary>
/// One request per second to MusicBrainz, across the whole process.
/// </summary>
/// <remarks>
/// <para>
/// Their published limit, and not a suggestion: they run on donated hardware and block clients
/// that exceed it. A per-user or per-request limiter would not do -- two users syncing at once
/// would double the rate, and the service sees one IP, not two people.
/// </para>
/// <para>
/// Registered as a singleton for exactly that reason. It is the one piece of this feature that
/// must not be scoped.
/// </para>
/// </remarks>
internal sealed class MusicBrainzRateLimiter : IDisposable
{
    /// <summary>The interval between requests. One per second, plus a little headroom.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1100);

    /// <summary>
    /// Serialises callers. A lock will not do: the wait is asynchronous, and holding a lock across
    /// an await is how a thread pool deadlocks.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// A monotonic clock, not wall time. A user adjusting the system clock, or a DST change,
    /// would otherwise either stall every lookup for an hour or let a burst straight through.
    /// </summary>
    private readonly Stopwatch _since = Stopwatch.StartNew();

    private TimeSpan _lastRequest = TimeSpan.MinValue;

    private readonly TimeSpan _interval;

    public MusicBrainzRateLimiter()
        : this(Interval)
    {
    }

    /// <summary>Test seam: the tests cannot afford to wait a real second per request.</summary>
    internal MusicBrainzRateLimiter(TimeSpan interval) => _interval = interval;

    /// <summary>Returns when it is this caller's turn to make a request.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var now = _since.Elapsed;
            var earliest = _lastRequest + _interval;

            if (_lastRequest != TimeSpan.MinValue && now < earliest)
            {
                await Task.Delay(earliest - now, cancellationToken);
            }

            // Stamped after the wait, so the interval is measured between requests leaving rather
            // than between callers arriving.
            _lastRequest = _since.Elapsed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
