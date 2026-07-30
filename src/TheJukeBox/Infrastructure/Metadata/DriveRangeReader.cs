using System.Net;
using System.Net.Http.Headers;
using TheJukeBox.Errors;
using TheJukeBox.Infrastructure.Drive;

namespace TheJukeBox.Infrastructure.Metadata;

/// <summary>
/// Supplies a Drive access token, refreshing it at most once per failure.
/// </summary>
/// <remarks>
/// <see cref="IDriveClientFactory.GetAccessTokenAsync"/> performs a full refresh exchange
/// with Google on every call, which is fine for a single stream request and ruinous for a
/// pass over thousands of files. Holding one token for the run and re-fetching only when
/// Drive rejects it turns thousands of token round trips into one or two.
/// </remarks>
internal sealed class DriveAccessToken(IDriveClientFactory factory, Guid userId)
{
    private string? _token;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_token is { Length: > 0 } cached)
        {
            return cached;
        }

        // Deliberately unguarded. A semaphore here would make the class own a disposable
        // field (CA1001) and require disposal plumbing through the whole pass, to prevent
        // an outcome that costs nothing: several parallel extractions starting at once may
        // each perform a refresh exchange, so the run does one or two extra token calls
        // instead of exactly one. Refresh is idempotent, and the point of caching was to
        // avoid thousands of round trips, not to reach precisely one.
        return _token = await factory.GetAccessTokenAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Discards the held token, so the next request obtains a fresh one.</summary>
    public void Invalidate() => _token = null;
}

/// <summary>
/// Reads byte ranges out of a Drive file with <c>alt=media</c> and a <c>Range</c> header.
/// </summary>
/// <remarks>
/// The same raw-<see cref="HttpClient"/> approach the streaming proxy uses, and for the
/// same reason: the generated Drive client insists on downloading into a stream it
/// controls, which is exactly the whole-file-into-memory behaviour this project removed.
/// The named client is shared with streaming so the handler and its connection pool are
/// reused rather than a new one being created per file.
/// </remarks>
internal sealed class DriveRangeReader(
    HttpClient client,
    DriveAccessToken accessToken,
    string driveFileId) : IRangeReader
{
    private const string MediaEndpoint = "https://www.googleapis.com/drive/v3/files";

    public async Task<int> ReadRangeAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(offset, destination.Length, refreshed: false, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            return await ReadFullyAsync(body, destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        long offset,
        int count,
        bool refreshed,
        CancellationToken cancellationToken)
    {
        var token = await accessToken.GetAsync(cancellationToken).ConfigureAwait(false);

        var uri = $"{MediaEndpoint}/{DriveQuery.RequireFileId(driveFileId)}?alt=media&supportsAllDrives=false";

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Inclusive on both ends, per RFC 9110. An off-by-one here silently shifts every
        // block after the first and produces garbage tags rather than an error.
        request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);

        var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        // A token that expired mid-run is the one failure worth retrying, and exactly
        // once: a second 401 means the grant itself is gone, not that the token was stale.
        if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
        {
            response.Dispose();
            accessToken.Invalidate();

            return await SendAsync(offset, count, refreshed: true, cancellationToken).ConfigureAwait(false);
        }

        var status = response.StatusCode;
        response.Dispose();

        throw status switch
        {
            HttpStatusCode.NotFound => new ResourceNotFoundException(
                "The file is no longer present in Drive."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new DriveConsentRequiredException(
                "Google refused access to this file. Please reconnect your Drive."),
            _ => new DriveUnavailableException($"Drive returned {(int)status} for a ranged media request."),
        };
    }

    /// <summary>
    /// Drains <paramref name="source"/> into <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// One HTTP response can arrive as many TCP reads, so a single
    /// <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/> is not enough. This
    /// is a network read loop, not a range request loop: it does not spend budget.
    /// </remarks>
    private static async Task<int> ReadFullyAsync(
        Stream source,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < destination.Length)
        {
            var read = await source.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
