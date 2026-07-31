using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheJukeBox.Diagnostics;
using TheJukeBox.Domain;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Library;

/// <summary>How often libraries refresh themselves.</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>
    /// Whether libraries refresh on a timer at all. Off means sync only happens when asked.
    /// </summary>
    public bool AutoSyncEnabled { get; init; } = true;

    /// <summary>
    /// Minutes between refreshes for a given user.
    /// </summary>
    /// <remarks>
    /// Fifteen is affordable because a refresh is an incremental change-feed read, which is a
    /// single request when nothing has happened. It would be indefensible against a full
    /// listing of a large library.
    /// </remarks>
    [Range(1, 24 * 60)]
    public int IntervalMinutes { get; init; } = 15;

    /// <summary>How often the scheduler looks for work. Not the per-user interval.</summary>
    [Range(1, 60)]
    public int TickMinutes { get; init; } = 1;

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    public TimeSpan Tick => TimeSpan.FromMinutes(TickMinutes);
}

/// <summary>
/// How long to wait after a run fails.
/// </summary>
/// <remarks>
/// Pure and public so the curve can be asserted directly. Backoff is the difference between a
/// user whose Drive grant has been revoked costing one failed request every few hours and
/// costing four every hour forever, which is how a self-hosted instance ends up rate-limited
/// for everyone on it.
/// </remarks>
public static class SyncBackoff
{
    /// <summary>The base unit. Quadratic growth means the first retry is still prompt.</summary>
    public static readonly TimeSpan Base = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Ceiling. A grant that has been revoked will never recover on its own, so retrying more
    /// often than a few times a day achieves nothing, but abandoning it entirely would mean a
    /// user who fixes the problem waits forever.
    /// </summary>
    public static readonly TimeSpan Maximum = TimeSpan.FromHours(6);

    public static TimeSpan For(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        // Squared in double, not in int: failures^2 overflows an int at ~46341 and would wrap
        // to a negative delay, turning a long-broken account into a hot loop.
        var minutes = Math.Pow(consecutiveFailures, 2) * Base.TotalMinutes;

        return minutes >= Maximum.TotalMinutes ? Maximum : TimeSpan.FromMinutes(minutes);
    }

    /// <summary>Whether a user whose last attempt failed may be retried yet.</summary>
    public static bool IsDue(DateTimeOffset? lastAttemptUtc, int consecutiveFailures, DateTimeOffset nowUtc)
    {
        if (consecutiveFailures <= 0 || lastAttemptUtc is null)
        {
            return true;
        }

        return nowUtc - lastAttemptUtc.Value >= For(consecutiveFailures);
    }
}

/// <summary>
/// Queues periodic refreshes for users whose libraries are due one.
/// </summary>
/// <remarks>
/// Queues rather than syncs. All the work happens in the single-consumer
/// <see cref="SyncWorker"/>, so the scheduler cannot cause concurrent writes no matter how
/// many users come due at once, and a slow sync delays the next tick's work rather than
/// overlapping with it.
/// </remarks>
internal sealed class SyncScheduler(
    IServiceScopeFactory scopeFactory,
    ISyncDispatcher dispatcher,
    IOptions<SyncOptions> options,
    ILogger<SyncScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutoSyncEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Tick);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await QueueDueUsersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // A failed tick must never end the scheduler; the next one may well succeed.
                Log.SyncFailed(logger, Guid.Empty, e);
            }
        }
    }

    private async Task QueueDueUsersAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<JukeBoxDbContext>();

        var now = DateTimeOffset.UtcNow;
        var cutoff = now - options.Value.Interval;

        // Only users with a usable Drive grant. Without that check, every revoked account
        // would be queued every interval to fail immediately.
        var candidates = await context.DriveSyncStates
            .AsNoTracking()
            .Where(s => s.Status != SyncStatus.Running
                        && s.Status != SyncStatus.Queued
                        && (s.LastIncrementalSyncAtUtc == null || s.LastIncrementalSyncAtUtc < cutoff)
                        && context.DriveTokens.Any(t => t.UserId == s.UserId && t.RevokedAtUtc == null))
            .Select(s => new
            {
                s.UserId,
                s.ConsecutiveFailures,
                s.LastIncrementalSyncAtUtc,
            })
            .ToListAsync(cancellationToken);

        var queued = 0;

        foreach (var candidate in candidates)
        {
            if (!SyncBackoff.IsDue(candidate.LastIncrementalSyncAtUtc, candidate.ConsecutiveFailures, now))
            {
                var backoffMinutes = SyncBackoff.For(candidate.ConsecutiveFailures).TotalMinutes;

                Log.SyncBackingOff(logger, candidate.UserId, candidate.ConsecutiveFailures, backoffMinutes);

                continue;
            }

            // Incremental. A scheduled refresh never forces a full listing; that is reserved
            // for the explicit rescan, and for recovery from an expired change token.
            if (dispatcher.TryEnqueue(candidate.UserId))
            {
                queued++;
            }
        }

        if (queued > 0)
        {
            Log.ScheduledSyncQueued(logger, queued);
        }
    }
}
