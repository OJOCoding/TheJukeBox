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
    /// <returns>True if this call queued new work.</returns>
    bool TryEnqueue(Guid userId);
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
    /// Users with work queued or running. Prevents a user hammering the sync button from
    /// filling the channel with copies of the same job.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public ChannelReader<Guid> Reader => _channel.Reader;

    public bool TryEnqueue(Guid userId)
    {
        if (!_pending.TryAdd(userId, 0))
        {
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

                // Pass one: enumerate Drive and reconcile rows, with metadata derived from
                // file names. Fast, and it leaves the library browsable and playable.
                await synchroniser.SynchroniseAsync(userId, stoppingToken);

                // Pass two: read embedded tags. Separate on purpose. Reading tags requires
                // fetching bytes from inside every file, so folding it into pass one would
                // mean a large library shows nothing for many minutes instead of appearing
                // within seconds and filling in.
                var enricher = scope.ServiceProvider.GetRequiredService<MetadataEnricher>();

                // No DriveSyncState passed: the synchroniser has already marked the run
                // Succeeded, and handing the enricher that row would have it reopen and
                // rewrite progress fields for a run that is finished.
                await enricher.EnrichAsync(userId, cancellationToken: stoppingToken);
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
