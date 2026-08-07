using System.Collections.Concurrent;
using System.Threading.Channels;
using TheJukeBox.Diagnostics;

namespace TheJukeBox.Features.Library;

/// <summary>Requests a library sync for a user.</summary>
public interface ISyncDispatcher
{
    /// <summary>
    /// Queues a sync, or folds the request into one already pending.
    /// </summary>
    /// <param name="forceFull">
    /// Discard the change token and relist everything. Folding a full request into a pending
    /// incremental one still upgrades it, so asking for a rescan is never silently downgraded
    /// to a change-feed read.
    /// </param>
    /// <returns>True if this call queued new work.</returns>
    bool TryEnqueue(Guid userId, bool forceFull = false);
}

/// <summary>
/// An in-process queue of pending syncs.
/// </summary>
/// <remarks>
/// No Hangfire, no Quartz, no Redis. A sync is idempotent and resumable from state
/// already in the database, which is precisely the property that makes a durable job
/// store unnecessary: if the process dies mid-run, the next run reaches the same result.
/// Adding an external scheduler would buy durability that is already there, at the cost
/// of more tables, another dashboard to secure, and a second scheduling model.
/// </remarks>
internal sealed class SyncDispatcher(ILogger<SyncDispatcher> logger) : ISyncDispatcher
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(64)
        {
            // Duplicate work is pointless, so a full queue drops rather than blocks the
            // request thread.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <summary>
    /// Users with work queued or running, and whether a full relist was asked for. Prevents a
    /// user hammering the sync button from filling the channel with copies of the same job.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, bool> _pending = new();

    public ChannelReader<Guid> Reader => _channel.Reader;

    public bool TryEnqueue(Guid userId, bool forceFull = false)
    {
        if (!_pending.TryAdd(userId, forceFull))
        {
            // Already pending. Upgrade rather than discard: a user who presses "rescan" while
            // a routine refresh is queued means it, and returning false here would otherwise
            // silently give them the change-feed read they were trying to bypass.
            if (forceFull)
            {
                _pending[userId] = true;
            }

            Log.SyncAlreadyPending(logger, userId);
            return false;
        }

        if (_channel.Writer.TryWrite(userId))
        {
            return true;
        }

        _pending.TryRemove(userId, out _);

        return false;
    }

    /// <summary>
    /// Whether the pending run for this user should relist everything.
    /// </summary>
    /// <remarks>
    /// Read at pickup rather than carried in the channel payload, so an upgrade that arrives
    /// after the job was queued is still honoured.
    /// </remarks>
    public bool IsForceFull(Guid userId) => _pending.TryGetValue(userId, out var forceFull) && forceFull;

    /// <summary>Called by the worker once a user's run has finished.</summary>
    public void Release(Guid userId) => _pending.TryRemove(userId, out _);

    public bool IsPending(Guid userId) => _pending.ContainsKey(userId);
}

/// <summary>
/// Runs queued syncs, one at a time.
/// </summary>
/// <remarks>
/// Deliberately a single consumer. SQLite tolerates one writer; parallelism belongs
/// inside a job, where the work is network-bound, not across jobs, where it would be
/// database-bound and produce SQLITE_BUSY for no throughput gain.
/// </remarks>
internal sealed class SyncWorker(
    SyncDispatcher queue,
    IServiceScopeFactory scopeFactory,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var userId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // A fresh scope per job: the synchroniser and its DbContext are scoped,
                // and a long-lived context would accumulate every track it ever tracked.
                using var scope = scopeFactory.CreateScope();

                var synchroniser = scope.ServiceProvider.GetRequiredService<LibrarySynchroniser>();

                // Read at pickup, so a rescan requested after this job was queued still wins.
                var forceFull = queue.IsForceFull(userId);

                // Pass one: reconcile rows against Drive, with metadata derived from file
                // names. Incremental unless a full relist was asked for. Fast either way, and
                // it leaves the library browsable and playable.
                await synchroniser.SynchroniseAsync(userId, forceFull, stoppingToken);

                // Pass two: read embedded tags. Separate on purpose. Reading tags requires
                // fetching bytes from inside every file, so folding it into pass one would
                // mean a large library shows nothing for many minutes instead of appearing
                // within seconds and filling in.
                var enricher = scope.ServiceProvider.GetRequiredService<MetadataEnricher>();

                // No DriveSyncState passed: the synchroniser has already marked the run
                // Succeeded, and handing the enricher that row would have it reopen and
                // rewrite progress fields for a run that is finished.
                await enricher.EnrichAsync(userId, cancellationToken: stoppingToken);

                // Pass three: ask an online database about what the files did not say. Last,
                // because it is the slowest by a wide margin -- one request per second, by the
                // service's terms -- and because it only wants to know what the first two passes
                // failed to establish. It returns immediately when the operator has not enabled it.
                var online = scope.ServiceProvider.GetRequiredService<OnlineMetadataEnricher>();

                await online.EnrichAsync(userId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Already recorded on the user's sync state by the synchroniser. Swallowed
                // here so one user's failure cannot take down the worker for everyone.
                Log.SyncFailed(logger, userId, e);
            }
            finally
            {
                queue.Release(userId);
            }
        }
    }
}
