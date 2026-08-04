using Microsoft.AspNetCore.Http.HttpResults;
using TheJukeBox.Errors;
using TheJukeBox.Features.Auth;

namespace TheJukeBox.Features.Playlists;

/// <summary>
/// Playlist CRUD, and the item operations that keep a playlist's order intact.
/// </summary>
public static class PlaylistEndpoints
{
    private const int MaxNameLength = 200;

    private const int MaxDescriptionLength = 1000;

    public static void MapPlaylistEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/playlists").RequireAuthorization().WithTags("Playlists");

        group.MapGet("/", async (
            ICurrentUser user,
            PlaylistService playlists,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await playlists.ListAsync(user.RequireId(), cancellationToken)));

        group.MapGet("/{id:guid}", GetAsync);

        // The player's entry point: the ids to queue, in order, already filtered to what can
        // actually be streamed.
        group.MapGet("/{id:guid}/tracks", async (
            Guid id,
            ICurrentUser user,
            PlaylistService playlists,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await playlists.TrackIdsAsync(user.RequireId(), id, cancellationToken)));

        group.MapPost("/", CreateAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        group.MapPost("/{id:guid}/tracks", AddTracksAsync);
        group.MapDelete("/{id:guid}/items/{itemId:guid}", RemoveItemAsync);

        // PUT, because it replaces the whole order rather than nudging one item. See
        // PlaylistService.ReorderAsync for why a full list beats a move instruction.
        group.MapPut("/{id:guid}/order", ReorderAsync);
    }

    private static async Task<Results<Ok<PlaylistDetail>, NotFound>> GetAsync(
        Guid id,
        ICurrentUser user,
        PlaylistService playlists,
        CancellationToken cancellationToken)
    {
        var detail = await playlists.GetAsync(user.RequireId(), id, cancellationToken);

        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Results<Created<PlaylistSummary>, ProblemHttpResult>> CreateAsync(
        CreatePlaylistRequest request,
        ICurrentUser user,
        PlaylistService playlists,
        CancellationToken cancellationToken)
    {
        if (Validate(request.Name, request.Description) is { } problem)
        {
            return problem;
        }

        var playlist = await playlists.CreateAsync(
            user.RequireId(),
            request.Name.Trim(),
            Clean(request.Description),
            cancellationToken);

        var summary = new PlaylistSummary(
            playlist.Id,
            playlist.Name,
            playlist.Description,
            playlist.CreatedAtUtc,
            playlist.UpdatedAtUtc,
            TrackCount: 0,
            DurationSeconds: 0,
            HasUnavailableTracks: false);

        return TypedResults.Created($"/api/playlists/{playlist.Id}", summary);
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdatePlaylistRequest request,
        ICurrentUser user,
        PlaylistService playlists,
        CancellationToken cancellationToken)
    {
        // Name is optional on a PATCH, so it is only validated when present. Description is
        // allowed to be an empty string, which is how it is cleared.
        if (request.Name is not null && Validate(request.Name, request.Description) is { } problem)
        {
            return problem;
        }

        var updated = await playlists.UpdateAsync(
            user.RequireId(),
            id,
            request.Name?.Trim(),
            request.Description,
            cancellationToken);

        return updated ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid id,
        ICurrentUser user,
        PlaylistService playlists,
        CancellationToken cancellationToken)
    {
        var deleted = await playlists.DeleteAsync(user.RequireId(), id, cancellationToken);

        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<AddResult>, NotFound, ProblemHttpResult>> AddTracksAsync(
        Guid id,
        TrackIdsRequest request,
        ICurrentUser user,
        PlaylistService playlists,
        CancellationToken cancellationToken)
    {
        if (request.TrackIds.Count == 0)
        {
            return Problem("A playlist addition needs at least one track id.");
        }

        var result = await playlists.AddTracksAsync(
            user.RequireId(),
            id,
            request.TrackIds,
            cancellationToken);

        return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
    }

    private static async Task<Results<NoContent, NotFound>> RemoveItemAsync(
        Guid id,
        Guid itemId,
        ICurrentUser user,
        PlaylistService playlists,
        CancellationToken cancellationToken)
    {
        var removed = await playlists.RemoveItemAsync(user.RequireId(), id, itemId, cancellationToken);

        return removed ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> ReorderAsync(
        Guid id,
        ItemOrderRequest request,
        ICurrentUser user,
        PlaylistService playlists,
        CancellationToken cancellationToken)
    {
        var reordered = await playlists.ReorderAsync(
            user.RequireId(),
            id,
            request.ItemIds,
            cancellationToken);

        // A mismatch is not "not found": the playlist exists, the client's picture of it is
        // stale. 409 tells the UI to reload rather than retry the same body.
        return reordered
            ? TypedResults.NoContent()
            : TypedResults.Problem(
                detail: "The supplied order does not match this playlist's current items. "
                        + "Reload it and try again.",
                statusCode: StatusCodes.Status409Conflict,
                title: "The playlist changed.",
                type: ProblemTypes.PlaylistOrderStale);
    }

    private static ProblemHttpResult? Validate(string? name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Problem("A playlist needs a name.");
        }

        if (name.Trim().Length > MaxNameLength)
        {
            return Problem($"A playlist name may be at most {MaxNameLength} characters.");
        }

        if (description?.Length > MaxDescriptionLength)
        {
            return Problem($"A playlist description may be at most {MaxDescriptionLength} characters.");
        }

        return null;
    }

    private static string? Clean(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static ProblemHttpResult Problem(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid playlist request.",
            type: ProblemTypes.Validation);
}

public sealed record CreatePlaylistRequest(string Name, string? Description);

/// <param name="Name">Absent leaves the name alone.</param>
/// <param name="Description">Absent leaves it alone; empty clears it.</param>
public sealed record UpdatePlaylistRequest(string? Name, string? Description);

public sealed record TrackIdsRequest(IReadOnlyList<Guid> TrackIds);

public sealed record ItemOrderRequest(IReadOnlyList<Guid> ItemIds);
