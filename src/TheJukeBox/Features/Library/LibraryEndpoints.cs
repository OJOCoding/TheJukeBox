using Microsoft.EntityFrameworkCore;
using TheJukeBox.Domain;
using TheJukeBox.Features.Auth;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Library;

/// <summary>
/// The cached library, and the controls for refreshing it.
/// </summary>
/// <remarks>
/// Reads the database, never Drive. That is what makes the library sortable,
/// searchable, fast on a second visit, and browsable while Drive is unreachable, and it
/// is what removes the 100-unit quota cost that the old live listing paid on every page
/// load.
/// </remarks>
public static class LibraryEndpoints
{
    /// <summary>Page size ceiling, so one request cannot ask for the whole table.</summary>
    private const int MaxPageSize = 500;

    private const int DefaultPageSize = 100;

    public static void MapLibraryEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api").RequireAuthorization().WithTags("Library");

        group.MapGet("/tracks", GetTracksAsync);
        group.MapGet("/sync/status", GetSyncStatusAsync);
        group.MapPost("/sync", (ICurrentUser user, ISyncDispatcher queue) => RequestSync(user, queue, forceFull: false));

        // The escape hatch. No incremental feed is trusted absolutely, so there is always a
        // way to discard the change token and relist from scratch.
        group.MapPost("/sync/full", (ICurrentUser user, ISyncDispatcher queue) => RequestSync(user, queue, forceFull: true));
    }

    private static async Task<IResult> GetTracksAsync(
        ICurrentUser currentUser,
        JukeBoxDbContext database,
        CancellationToken cancellationToken,
        string? q = null,
        string? sort = null,
        int? limit = null,
        int? offset = null,
        bool includeUnavailable = false)
    {
        var userId = currentUser.RequireId();
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);

        var query = database.Tracks.AsNoTracking().Where(t => t.UserId == userId);

        if (!includeUnavailable)
        {
            query = query.Where(t => t.DeletedAtUtc == null);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            // Matched against the precomputed, accent-folded SearchText rather than
            // normalising several columns per row per keystroke.
            var needle = SearchNeedle(q);

            query = query.Where(t => EF.Functions.Like(t.SearchText, $"%{needle}%"));
        }

        var total = await query.CountAsync(cancellationToken);

        var page = await Order(query, sort)
            .Skip(skip)
            .Take(take)
            .Select(t => new TrackDto(
                t.Id,
                t.Title,
                t.Artist,
                t.Album,
                t.TrackNumber,
                t.DurationSeconds,
                t.SizeBytes,
                t.FileExtension,
                t.MimeType,
                t.MetadataSource.ToString(),
                t.ArtSha256,
                t.DeletedAtUtc == null,
                $"/api/tracks/{t.Id}/stream"))
            .ToListAsync(cancellationToken);

        return Results.Ok(new TrackPageDto(total, skip, take, page));
    }

    private static IQueryable<Track> Order(IQueryable<Track> query, string? sort) => sort switch
    {
        "artist" => query.OrderBy(t => t.Artist).ThenBy(t => t.Album).ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.Title),
        "album" => query.OrderBy(t => t.Album).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.Title),
        "added" => query.OrderByDescending(t => t.AddedAtUtc).ThenBy(t => t.Title),
        "duration" => query.OrderBy(t => t.DurationSeconds).ThenBy(t => t.Title),

        // Title, and a tiebreak on Id. Without a deterministic tiebreak, two tracks with
        // the same title can swap places between pages, so paging silently shows one
        // twice and skips another.
        _ => query.OrderBy(t => t.Title).ThenBy(t => t.Id),
    };

    /// <summary>
    /// Folds a query the same way <c>SearchText</c> was folded, and escapes LIKE
    /// wildcards so a literal % or _ does not match everything.
    /// </summary>
    private static string SearchNeedle(string query)
    {
        var trimmed = query.Length > 128 ? query[..128] : query;

        return trimmed
            .ToLowerInvariant()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static async Task<IResult> GetSyncStatusAsync(
        ICurrentUser currentUser,
        JukeBoxDbContext database,
        ISyncDispatcher queue,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireId();

        var state = await database.DriveSyncStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

        var trackCount = await database.Tracks
            .CountAsync(t => t.UserId == userId && t.DeletedAtUtc == null, cancellationToken);

        return Results.Ok(new SyncStatusDto(
            state?.Status.ToString() ?? nameof(SyncStatus.Idle),
            state?.Phase,
            state?.ItemsProcessed ?? 0,
            state?.ItemsTotal ?? 0,
            trackCount,
            state?.LastFullSyncAtUtc,
            state?.ConsecutiveFailures ?? 0,
            state?.LastError,
            queue is SyncDispatcher concrete && concrete.IsPending(userId)));
    }

    /// <summary>
    /// Asks for a sync. Idempotent: repeating it while one is pending is not an error.
    /// </summary>
    private static IResult RequestSync(ICurrentUser currentUser, ISyncDispatcher queue, bool forceFull)
    {
        var queued = queue.TryEnqueue(currentUser.RequireId(), forceFull);

        // 202 either way. The caller's intent -- "make sure my library is fresh" -- is
        // satisfied by an already-running sync just as well as by a new one, so
        // reporting a conflict would only invite pointless retry logic.
        return Results.Accepted(value: new { queued });
    }
}

public sealed record TrackDto(
    Guid Id,
    string Title,
    string? Artist,
    string? Album,
    int? TrackNumber,
    int? DurationSeconds,
    long SizeBytes,
    string? Extension,
    string? MimeType,
    string MetadataSource,

    /// <summary>
    /// Content hash of the embedded cover image, or null when the file had none.
    /// </summary>
    /// <remarks>
    /// The hash rather than a per-track art URL: every track on an album shares one image,
    /// so the client requests and caches a single URL for the whole album.
    /// </remarks>
    string? ArtSha256,

    bool IsAvailable,
    string StreamUrl);

public sealed record TrackPageDto(int Total, int Offset, int Limit, IReadOnlyList<TrackDto> Items);

public sealed record SyncStatusDto(
    string Status,
    string? Phase,
    int ItemsProcessed,
    int ItemsTotal,
    int TrackCount,
    DateTimeOffset? LastFullSyncAtUtc,
    int ConsecutiveFailures,
    string? LastError,
    bool Pending);
