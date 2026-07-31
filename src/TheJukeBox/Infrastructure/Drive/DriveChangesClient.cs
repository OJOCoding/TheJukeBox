using Google;
using TheJukeBox.Diagnostics;
using DriveData = Google.Apis.Drive.v3.Data;

namespace TheJukeBox.Infrastructure.Drive;

/// <summary>
/// One entry from Drive's change feed.
/// </summary>
/// <param name="Removed">
/// True when the file is gone or no longer visible to this user. Drive reports deletions and
/// lost access identically, and for our purposes they are the same thing.
/// </param>
/// <param name="File">
/// The file's current state, or null when it was removed. Present but non-audio also means
/// "drop it": a file retyped away from audio, or renamed from .mp3 to .txt, must leave the
/// library.
/// </param>
public sealed record DriveChange(string DriveFileId, bool Removed, DriveAudioFile? File)
{
    /// <summary>Whether this change means the track should leave the library.</summary>
    public bool IsRemoval => Removed || File is null;
}

/// <summary>One page of the change feed.</summary>
/// <param name="NewStartPageToken">
/// Present only on the final page. It is the token to store for the next run, and its
/// absence mid-feed is why the loop must continue on <paramref name="NextPageToken"/>.
/// </param>
public sealed record DriveChangePage(
    IReadOnlyList<DriveChange> Changes,
    string? NextPageToken,
    string? NewStartPageToken);

/// <summary>
/// Thrown when Drive rejects a stored page token.
/// </summary>
/// <remarks>
/// Change tokens are long-lived but not eternal. Without a recovery path, "automatic sync"
/// works for months and then stops permanently with no visible cause, which is precisely the
/// kind of failure that makes a self-hosted app look broken. The caller falls back to a full
/// listing and starts a fresh token.
/// </remarks>
public sealed class DriveChangeTokenExpiredException(string message) : Exception(message);

/// <summary>Reads Drive's change feed for a user.</summary>
public interface IDriveChangesClient
{
    /// <summary>
    /// A token marking "now", to be stored before a full listing so nothing that happens
    /// during the listing is missed.
    /// </summary>
    Task<string> GetStartPageTokenAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Fetches one page of changes.</summary>
    /// <exception cref="DriveChangeTokenExpiredException">The token is no longer valid.</exception>
    Task<DriveChangePage> GetChangesAsync(
        Guid userId,
        string pageToken,
        CancellationToken cancellationToken = default);
}

internal sealed class DriveChangesClient(
    IDriveClientFactory driveClientFactory,
    ILogger<DriveChangesClient> logger) : IDriveChangesClient
{
    private const int PageSize = 1000;

    private const string ChangeFields =
        "nextPageToken, newStartPageToken, "
        + "changes(fileId, removed, time, "
        + "file(id, name, mimeType, size, md5Checksum, headRevisionId, parents, "
        + "createdTime, modifiedTime, trashed))";

    public async Task<string> GetStartPageTokenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var drive = await driveClientFactory.CreateAsync(userId, cancellationToken);

        var request = drive.Changes.GetStartPageToken();
        request.SupportsAllDrives = false;

        var response = await request.ExecuteAsync(cancellationToken);

        return response.StartPageTokenValue
               ?? throw new DriveChangeTokenExpiredException("Drive returned no start page token.");
    }

    public async Task<DriveChangePage> GetChangesAsync(
        Guid userId,
        string pageToken,
        CancellationToken cancellationToken = default)
    {
        var drive = await driveClientFactory.CreateAsync(userId, cancellationToken);

        var request = drive.Changes.List(pageToken);
        request.Fields = ChangeFields;
        request.PageSize = PageSize;

        // Removals are the whole point of an incremental pass; without this a deleted file
        // would linger in the library until the next full sync.
        request.IncludeRemoved = true;

        // Matches the full listing's scope. Shared drives are out of scope, and mixing
        // scopes between the two passes would make files appear and disappear.
        request.RestrictToMyDrive = true;
        request.SupportsAllDrives = false;
        request.Spaces = "drive";

        DriveData.ChangeList response;

        try
        {
            response = await request.ExecuteAsync(cancellationToken);
        }
        catch (GoogleApiException e) when (IsExpiredToken(e))
        {
            Log.ChangeTokenExpired(logger, userId);

            throw new DriveChangeTokenExpiredException(
                "Drive rejected the stored change token; a full resynchronisation is required.");
        }

        var changes = new List<DriveChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Reversed, then deduplicated by file id, keeping the LAST entry for each file.
        // Drive can report several changes for one file in a single page, and applying the
        // earlier ones after the later one would leave stale state -- a file that was
        // renamed and then trashed would end up present under its new name.
        foreach (var change in (response.Changes ?? []).Reverse())
        {
            var fileId = change.FileId ?? change.File?.Id;

            if (fileId is null || !seen.Add(fileId))
            {
                continue;
            }

            var removed = change.Removed == true
                          || change.File is null
                          || change.File.Trashed == true
                          || !IsAudio(change.File.MimeType);

            changes.Add(new DriveChange(fileId, removed, removed ? null : Map(change.File!)));
        }

        // Restored to feed order for readability; order no longer matters after dedup.
        changes.Reverse();

        Log.ChangesFetched(logger, userId, changes.Count, response.NewStartPageToken is not null);

        return new DriveChangePage(changes, response.NextPageToken, response.NewStartPageToken);
    }

    /// <summary>
    /// Whether Drive is telling us the token is unusable.
    /// </summary>
    /// <remarks>
    /// A 404 is the documented response for a stale token. A 400 with a reason mentioning the
    /// page token covers the case where Drive reports it as malformed instead, which happens
    /// with a token from a different account or a truncated value.
    /// </remarks>
    private static bool IsExpiredToken(GoogleApiException e) =>
        e.HttpStatusCode == System.Net.HttpStatusCode.NotFound
        || (e.HttpStatusCode == System.Net.HttpStatusCode.BadRequest
            && e.Message.Contains("pageToken", StringComparison.OrdinalIgnoreCase));

    private static bool IsAudio(string? mimeType) =>
        mimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

    private static DriveAudioFile Map(DriveData.File file) => new(
        file.Id!,
        file.Name ?? string.Empty,
        file.MimeType,
        file.Size ?? 0,
        file.Md5Checksum,
        file.HeadRevisionId,
        file.Parents?.FirstOrDefault(),
        file.CreatedTimeDateTimeOffset,
        file.ModifiedTimeDateTimeOffset);
}
