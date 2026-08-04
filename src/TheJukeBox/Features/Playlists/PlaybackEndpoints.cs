using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TheJukeBox.Domain;
using TheJukeBox.Features.Auth;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Playlists;

/// <summary>
/// The saved queue, and the listening log.
/// </summary>
/// <remarks>
/// Two endpoints that look similar and are not: <c>/api/playback</c> is a single row rewritten
/// in place so a reload resumes where the user was, and <c>/api/history</c> is an append-only
/// log of what they actually listened to.
/// </remarks>
public static class PlaybackEndpoints
{
    /// <summary>How many history rows one request may ask for.</summary>
    private const int MaxHistoryPageSize = 200;

    public static void MapPlaybackEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api").RequireAuthorization().WithTags("Playback");

        group.MapGet("/playback", GetPlaybackAsync);
        group.MapPut("/playback", SavePlaybackAsync);

        group.MapGet("/history", GetHistoryAsync);
        group.MapPost("/history", RecordPlayAsync);
    }

    /// <summary>
    /// Where the user was.
    /// </summary>
    /// <remarks>
    /// Queued ids are filtered against the library on the way out, so a queue holding files
    /// that were trashed since it was saved comes back shorter rather than coming back with
    /// entries that 404 when played. The cursor is moved to match, because dropping earlier
    /// entries would otherwise silently shift what "resume" means.
    /// </remarks>
    private static async Task<Ok<PlaybackStateDto>> GetPlaybackAsync(
        ICurrentUser user,
        JukeBoxDbContext database,
        CancellationToken cancellationToken)
    {
        var userId = user.RequireId();

        var state = await database.PlaybackStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

        if (state is null)
        {
            return TypedResults.Ok(PlaybackStateDto.Empty);
        }

        var saved = TrackIdCodec.Decode(state.QueuedTrackIds);

        // The tracks themselves, not just their ids. The client cannot resolve an id it has not
        // already loaded, and a restored queue routinely reaches beyond the first page of the
        // library -- so returning ids alone would silently drop most of a large queue. This also
        // doubles as the ownership check: another user's ids do not come back.
        var playable = await database.Tracks
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.DeletedAtUtc == null && saved.Contains(t.Id))
            .Select(t => new QueuedTrackDto(
                t.Id,
                t.Title,
                t.Artist,
                t.Album,
                t.DurationSeconds,
                t.ArtSha256))
            .ToListAsync(cancellationToken);

        var byId = playable.ToDictionary(t => t.Id);

        var (queue, cursor, position) = QueueReconciliation.Apply(
            saved,
            byId.Keys.ToHashSet(),
            state.Cursor,
            state.PositionSeconds);

        return TypedResults.Ok(new PlaybackStateDto(
            [.. queue.Select(id => id.ToString())],

            // Distinct, because a queue may hold the same track twice but the catalogue is keyed
            // by id and does not need it twice.
            [.. queue.Distinct().Select(id => byId[id])],
            cursor,
            position,
            state.Shuffle,
            state.Repeat.ToString(),
            state.UpdatedAtUtc));
    }

    /// <summary>
    /// Saves the queue. Called often, so it is one upsert and nothing else.
    /// </summary>
    private static async Task<NoContent> SavePlaybackAsync(
        SavePlaybackRequest request,
        ICurrentUser user,
        JukeBoxDbContext database,
        CancellationToken cancellationToken)
    {
        var userId = user.RequireId();

        var state = await database.PlaybackStates
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

        if (state is null)
        {
            state = new PlaybackState { UserId = userId };
            database.PlaybackStates.Add(state);
        }

        var ids = request.TrackIds ?? [];

        // Ids are stored as given, without checking they exist. The read path filters instead:
        // validating here would mean a query against the whole library on a call that happens
        // every few seconds, to prevent something the reader already handles.
        state.QueuedTrackIds = TrackIdCodec.Encode(
            [.. ids.Take(PlaybackState.MaxQueueLength)
                .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)]);

        var length = state.QueuedTrackIds.Length / 32;

        // Clamped rather than trusted. A cursor past the end of the queue would make the resume
        // path read out of bounds, and the client is not the authority on this row's shape.
        state.Cursor = request.Cursor >= 0 && request.Cursor < length ? request.Cursor : -1;

        state.PositionSeconds = double.IsFinite(request.PositionSeconds)
            ? Math.Clamp(request.PositionSeconds, 0, TimeSpan.FromDays(1).TotalSeconds)
            : 0;

        state.Shuffle = request.Shuffle;

        state.Repeat = Enum.TryParse<RepeatMode>(request.Repeat, ignoreCase: true, out var repeat)
                       && Enum.IsDefined(repeat)
            ? repeat
            : RepeatMode.Off;

        state.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await database.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<HistoryEntryDto>>> GetHistoryAsync(
        ICurrentUser user,
        JukeBoxDbContext database,
        CancellationToken cancellationToken,
        int? limit = null)
    {
        var userId = user.RequireId();
        var take = Math.Clamp(limit ?? 50, 1, MaxHistoryPageSize);

        var entries = await database.PlayHistory
            .AsNoTracking()
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.PlayedAtUtc)
            .Take(take)
            .Select(h => new HistoryEntryDto(
                h.Id,
                h.TrackId,
                h.Track!.Title,
                h.Track.Artist,
                h.Track.Album,
                h.Track.ArtSha256,
                h.PlayedAtUtc,
                h.SecondsPlayed,
                h.Completed,
                h.Track.DeletedAtUtc == null))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok<IReadOnlyList<HistoryEntryDto>>(entries);
    }

    /// <summary>
    /// Records one listen, and keeps the counters on the track in step.
    /// </summary>
    /// <remarks>
    /// The client decides when a play counts (see <see cref="PlayHistory.MinimumSeconds"/>),
    /// because only it knows how much audio actually played. The server still enforces the
    /// threshold: a client bug that posted on every skip would otherwise fill the log with
    /// tracks the user rejected, and there would be no way to tell afterwards.
    /// </remarks>
    private static async Task<Results<NoContent, NotFound>> RecordPlayAsync(
        RecordPlayRequest request,
        ICurrentUser user,
        JukeBoxDbContext database,
        CancellationToken cancellationToken)
    {
        var userId = user.RequireId();

        var seconds = Math.Clamp(request.SecondsPlayed, 0, (int)TimeSpan.FromDays(1).TotalSeconds);

        // A completed track counts however short it was: a 12-second interlude played to the
        // end is a listen, and the threshold exists to exclude skips, not short tracks.
        if (seconds < PlayHistory.MinimumSeconds && !request.Completed)
        {
            return TypedResults.NoContent();
        }

        // The ownership check. A track id belonging to someone else does not come back, so this
        // cannot be used to increment another user's counters or probe which ids exist.
        var track = await database.Tracks
            .FirstOrDefaultAsync(t => t.Id == request.TrackId && t.UserId == userId, cancellationToken);

        if (track is null)
        {
            return TypedResults.NotFound();
        }

        var now = DateTimeOffset.UtcNow;

        database.PlayHistory.Add(new PlayHistory
        {
            UserId = userId,
            TrackId = track.Id,
            PlayedAtUtc = now,
            SecondsPlayed = seconds,
            Completed = request.Completed,
        });

        track.PlayCount++;
        track.LastPlayedAtUtc = now;

        // Deliberately not bumping RowVersion. That counter drives delta sync against Drive,
        // and listening to a track is not a change to the file -- bumping it would make every
        // play look like a library change to any client polling for one.
        await database.SaveChangesAsync(cancellationToken);

        await PruneHistoryAsync(database, userId, cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Trims the log back to <see cref="PlayHistory.MaxRowsPerUser"/>.
    /// </summary>
    /// <remarks>
    /// Runs after the insert rather than on a schedule, because a background pruner is a whole
    /// hosted service for a table that grows a few hundred rows a week. The count is an index
    /// seek and plays are minutes apart, so the cost is irrelevant; the batch threshold just
    /// stops it deleting one row at a time forever once the cap is reached.
    /// </remarks>
    private static async Task PruneHistoryAsync(
        JukeBoxDbContext database,
        Guid userId,
        CancellationToken cancellationToken)
    {
        const int slack = 500;

        var total = await database.PlayHistory.CountAsync(h => h.UserId == userId, cancellationToken);

        if (total <= PlayHistory.MaxRowsPerUser + slack)
        {
            return;
        }

        var excess = total - PlayHistory.MaxRowsPerUser;

        // ExecuteDelete: a set-based delete, so trimming a thousand rows does not load a
        // thousand entities into the change tracker to throw them away.
        var doomed = database.PlayHistory
            .Where(h => h.UserId == userId)
            .OrderBy(h => h.PlayedAtUtc)
            .Take(excess)
            .Select(h => h.Id);

        await database.PlayHistory
            .Where(h => doomed.Contains(h.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}

/// <param name="TrackIds">
/// Sent as strings rather than GUIDs so one malformed entry from a stale client drops that
/// entry instead of failing the whole save with a binding error.
/// </param>
public sealed record SavePlaybackRequest(
    IReadOnlyList<string>? TrackIds,
    int Cursor,
    double PositionSeconds,
    bool Shuffle,
    string? Repeat);

/// <param name="Tracks">
/// Metadata for the queued ids, so the client can label and stream them without having loaded
/// the part of the library they came from.
/// </param>
public sealed record PlaybackStateDto(
    IReadOnlyList<string> TrackIds,
    IReadOnlyList<QueuedTrackDto> Tracks,
    int Cursor,
    double PositionSeconds,
    bool Shuffle,
    string Repeat,
    DateTimeOffset? UpdatedAtUtc)
{
    public static PlaybackStateDto Empty { get; } =
        new([], [], -1, 0, false, nameof(RepeatMode.Off), null);
}

/// <summary>
/// The subset of a track the transport and the queue view need.
/// </summary>
/// <remarks>
/// Deliberately not <c>TrackDto</c>: that carries sizes, mime types and metadata provenance the
/// queue has no use for, and a saved queue can be two thousand entries long.
/// </remarks>
public sealed record QueuedTrackDto(
    Guid Id,
    string Title,
    string? Artist,
    string? Album,
    int? DurationSeconds,
    string? ArtSha256);

public sealed record RecordPlayRequest(Guid TrackId, int SecondsPlayed, bool Completed);

public sealed record HistoryEntryDto(
    Guid Id,
    Guid TrackId,
    string Title,
    string? Artist,
    string? Album,
    string? ArtSha256,
    DateTimeOffset PlayedAtUtc,
    int SecondsPlayed,
    bool Completed,
    bool IsAvailable);
