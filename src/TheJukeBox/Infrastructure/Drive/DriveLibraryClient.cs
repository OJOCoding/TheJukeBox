using Google.Apis.Drive.v3;
using TheJukeBox.Diagnostics;
using DriveData = Google.Apis.Drive.v3.Data;

namespace TheJukeBox.Infrastructure.Drive;

/// <summary>One audio file as Drive described it.</summary>
public sealed record DriveAudioFile(
    string DriveFileId,
    string Name,
    string? MimeType,
    long SizeBytes,
    string? Md5Checksum,
    string? HeadRevisionId,
    string? ParentFolderId,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? ModifiedAtUtc);

/// <summary>Enumerates a user's audio files from Drive.</summary>
public interface IDriveLibraryClient
{
    /// <summary>
    /// Every audio file in the user's Drive, following pagination to the end.
    /// </summary>
    IAsyncEnumerable<DriveAudioFile> EnumerateAudioAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves folder names for the given ids, for album fallback.</summary>
    Task<Dictionary<string, string>> ResolveFolderNamesAsync(
        Guid userId,
        IReadOnlyCollection<string> folderIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IDriveLibraryClient"/> over the Drive v3 API.
/// </summary>
/// <remarks>
/// This is the class that fixes the truncation bug. The previous listing set
/// <c>PageSize = 100</c> and never read <c>NextPageToken</c>, so a library of any size
/// silently stopped at 100 files: tracks beyond that were absent from the UI and
/// returned "File not found" when addressed directly, despite streaming perfectly.
/// Nothing reported an error, which is why it survived.
/// </remarks>
internal sealed class DriveLibraryClient(
    IDriveClientFactory driveClientFactory,
    ILogger<DriveLibraryClient> logger) : IDriveLibraryClient
{
    /// <summary>
    /// Drive's documented maximum. Fewer, larger pages means fewer round trips and
    /// fewer quota units for the same result: a 5,000-track library is 5 calls at 1000
    /// rather than 50 at 100.
    /// </summary>
    private const int PageSize = 1000;

    private const string ListFields =
        "nextPageToken, files(id, name, mimeType, size, md5Checksum, headRevisionId, "
        + "parents, createdTime, modifiedTime, trashed)";

    public async IAsyncEnumerable<DriveAudioFile> EnumerateAudioAsync(
        Guid userId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var drive = await driveClientFactory.CreateAsync(userId, cancellationToken);

        string? pageToken = null;
        var pages = 0;
        var returned = 0;

        do
        {
            var request = drive.Files.List();
            request.Q = DriveQuery.AllAudio();
            request.Fields = ListFields;
            request.PageSize = PageSize;
            request.PageToken = pageToken;

            // Ordering by name gives a stable enumeration, which matters because a
            // reconcile pass compares against what it saw last time.
            request.OrderBy = "name";

            // Shared drives and files with several parents are out of scope for now.
            // Stated explicitly so it reads as a decision rather than an oversight.
            request.SupportsAllDrives = false;

            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var file in response.Files ?? [])
            {
                if (file.Id is null || file.Trashed == true)
                {
                    continue;
                }

                returned++;

                yield return Map(file);
            }

            pageToken = response.NextPageToken;
            pages++;
        }
        while (!string.IsNullOrEmpty(pageToken));

        Log.DriveEnumerated(logger, returned, pages);
    }

    public async Task<Dictionary<string, string>> ResolveFolderNamesAsync(
        Guid userId,
        IReadOnlyCollection<string> folderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderIds);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        if (folderIds.Count == 0)
        {
            return names;
        }

        var drive = await driveClientFactory.CreateAsync(userId, cancellationToken);

        // One get per distinct folder, not per track. A library of thousands of files
        // typically has dozens of folders, so this is cheap; batching would be cheaper
        // still and is a later refinement.
        foreach (var folderId in folderIds.Where(DriveQuery.IsValidFileId).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var request = drive.Files.Get(folderId);
                request.Fields = "id, name";

                var folder = await request.ExecuteAsync(cancellationToken);

                if (folder.Name is not null)
                {
                    names[folderId] = folder.Name;
                }
            }
            catch (Google.GoogleApiException e)
                when (e.HttpStatusCode is System.Net.HttpStatusCode.NotFound
                          or System.Net.HttpStatusCode.Forbidden)
            {
                // A parent we cannot read is not a sync failure. The album falls back to
                // whatever the file name suggests.
            }
        }

        return names;
    }

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
