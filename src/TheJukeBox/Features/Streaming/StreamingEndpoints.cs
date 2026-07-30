using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using TheJukeBox.Errors;
using TheJukeBox.Features.Auth;
using TheJukeBox.Infrastructure.Drive;
using TheJukeBox.Infrastructure.Metadata;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Streaming;

/// <summary>
/// Streams audio by proxying Drive, forwarding the client's <c>Range</c> header.
/// </summary>
/// <remarks>
/// <para>
/// Replaces a download action that buffered the entire file into a
/// <see cref="MemoryStream"/> and then let the framework slice it. That cost the whole
/// file in server memory per concurrent listener, made time-to-first-byte equal to full
/// download time, and re-downloaded the entire track from Google on every seek. It also
/// ran a full Drive listing on each request purely to look up a MIME type.
/// </para>
/// <para>
/// Here the range is forwarded upstream and the response body is piped through with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so memory is one buffer
/// regardless of file size and a seek fetches only what was asked for. Content type,
/// length and ETag all come from the cached track row, so a stream request costs zero
/// Drive metadata calls.
/// </para>
/// </remarks>
public static class StreamingEndpoints
{
    /// <summary>Named client, so the handler and its connection pool are reused.</summary>
    public const string HttpClientName = "drive-media";

    private const string MediaEndpoint = "https://www.googleapis.com/drive/v3/files";

    public static void MapStreamingEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapMethods("/api/tracks/{trackId:guid}/stream", ["GET", "HEAD"], StreamAsync)
            .RequireAuthorization()
            .WithTags("Streaming");
    }

    private static async Task<IResult> StreamAsync(
        Guid trackId,
        HttpContext httpContext,
        ICurrentUser currentUser,
        JukeBoxDbContext database,
        IDriveClientFactory driveClientFactory,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireId();

        // Scoped to the caller. A track belonging to someone else is reported as missing
        // rather than forbidden: confirming that an id exists but is not yours is itself
        // a disclosure. This single predicate is what closes the previous design's IDOR,
        // where any caller could stream any Drive file id it could guess.
        var track = await database.Tracks
            .AsNoTracking()
            .Where(t => t.Id == trackId && t.UserId == userId && t.DeletedAtUtc == null)
            .Select(t => new
            {
                t.DriveFileId,
                t.SizeBytes,
                t.MimeType,
                t.FileExtension,
                t.HeadRevisionId,
                t.DriveModifiedAtUtc,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (track is null)
        {
            throw new ResourceNotFoundException("No such track.");
        }

        var contentType = AudioFormats.ContentTypeFor(track.FileExtension);
        var resolved = RangeResolver.Resolve(httpContext.Request.Headers.Range, track.SizeBytes);

        var response = httpContext.Response;

        // Drive's revision id changes whenever the file's content does, which is exactly
        // the semantics of a strong ETag.
        if (track.HeadRevisionId is { Length: > 0 } revision)
        {
            response.Headers.ETag = $"\"{revision}\"";
        }

        // Always advertised, on every response including 416. A client that does not see
        // this assumes seeking is impossible and disables the seek bar.
        response.Headers.AcceptRanges = "bytes";

        if (resolved.Outcome == RangeOutcome.Unsatisfiable)
        {
            // The Content-Range on a 416 reports the entity length with an unknown range,
            // which is how a client learns the real size and corrects itself.
            response.Headers.ContentRange = $"bytes */{track.SizeBytes}";

            return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        var isPartial = resolved.Outcome == RangeOutcome.Partial;

        response.StatusCode = isPartial
            ? StatusCodes.Status206PartialContent
            : StatusCodes.Status200OK;

        response.ContentType = contentType;
        response.ContentLength = isPartial ? resolved.Range.Length : track.SizeBytes;

        if (isPartial)
        {
            response.Headers.ContentRange = resolved.Range.ToContentRange(track.SizeBytes);
        }

        // Cacheable by the browser but revalidated, so the warm-the-next-track trick works
        // while a changed file is still picked up. Explicitly not no-store, which would
        // defeat it entirely.
        response.Headers.CacheControl = "private, max-age=0, must-revalidate";

        // A HEAD must produce identical headers and no body. Media elements use it to
        // learn the length before deciding how to seek.
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return Results.Empty;
        }

        await PipeAsync(
            userId,
            track.DriveFileId,
            isPartial ? resolved.Range : null,
            driveClientFactory,
            httpClientFactory,
            response,
            cancellationToken);

        return Results.Empty;
    }

    private static async Task PipeAsync(
        Guid userId,
        string driveFileId,
        ByteRange? range,
        IDriveClientFactory driveClientFactory,
        IHttpClientFactory httpClientFactory,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var accessToken = await driveClientFactory.GetAccessTokenAsync(userId, cancellationToken);

        var client = httpClientFactory.CreateClient(HttpClientName);

        var uri = $"{MediaEndpoint}/{DriveQuery.RequireFileId(driveFileId)}?alt=media&supportsAllDrives=false";

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (range is { } window)
        {
            request.Headers.Range = new RangeHeaderValue(window.From, window.To);
        }

        // ResponseHeadersRead is the whole point: without it HttpClient buffers the entire
        // response before returning, which is the behaviour being removed.
        using var upstream = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!upstream.IsSuccessStatusCode)
        {
            throw upstream.StatusCode switch
            {
                HttpStatusCode.NotFound => new ResourceNotFoundException(
                    "The file is no longer present in Drive."),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new DriveConsentRequiredException(
                        "Google refused access to this file. Please reconnect your Drive."),
                _ => new DriveUnavailableException(
                    $"Drive returned {(int)upstream.StatusCode} for a media request."),
            };
        }

        await using var body = await upstream.Content.ReadAsStreamAsync(cancellationToken);

        // The cancellation token is plumbed all the way through, so skipping a track
        // aborts the upstream fetch rather than leaving it to run to completion against a
        // client that has gone away.
        await body.CopyToAsync(response.Body, cancellationToken);
    }
}
