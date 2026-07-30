using Google.Apis.Drive.v3;
using TheJukeBox.Diagnostics;
using TheJukeBox.Features.Auth;
using TheJukeBox.Infrastructure.Drive;
using DriveData = Google.Apis.Drive.v3.Data;

namespace TheJukeBox.API;

public interface IGoogleDriveService
{
    Task<List<AudioFile>> GetAudioFilesAsync(CancellationToken cancellationToken = default);

    Task<List<AudioFile>> GetAudioFilesInFolderAsync(string folderId, CancellationToken cancellationToken = default);

    Task<AudioFile?> GetAudioFileAsync(string fileId, CancellationToken cancellationToken = default);

    Task<Stream> DownloadAudioFileAsync(string fileId, CancellationToken cancellationToken = default);

    Task<List<DriveFile>> SearchAudioFilesAsync(string? searchTerm = null, CancellationToken cancellationToken = default);

    Task<List<DriveFile>> SearchFilesByNameAsync(string? fileName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads audio files from Google Drive. Superseded in A5 (listing) and A6 (streaming).
/// </summary>
/// <remarks>
/// Two known defects are deliberately left in place because the phases that fix them
/// replace this class entirely, so patching here would be thrown away: listings do
/// not follow <c>NextPageToken</c> and therefore truncate at 100 files, and
/// <see cref="DownloadAudioFileAsync"/> buffers whole files into memory. What is
/// fixed is query construction, which was injectable, and the use of obsolete Drive
/// date properties.
/// </remarks>
public sealed class GoogleDriveService(
    IDriveClientFactory driveClientFactory,
    ICurrentUser currentUser,
    ILogger<GoogleDriveService> logger) : IGoogleDriveService
{
    /// <summary>
    /// A Drive client for the user this request is acting as.
    /// </summary>
    /// <remarks>
    /// Every Drive call routes through here, so the account whose Drive is read is
    /// always the authenticated caller's. Previously all callers shared one token
    /// stored under a fixed key, meaning any request read the instance owner's library.
    /// </remarks>
    private Task<DriveService> DriveAsync(CancellationToken cancellationToken) =>
        driveClientFactory.CreateAsync(currentUser.RequireId(), cancellationToken);

    private const string AudioFields =
        "files(id, name, mimeType, size, createdTime, modifiedTime, webContentLink)";

    private const string SingleFileFields =
        "id, name, mimeType, size, createdTime, modifiedTime, webContentLink";

    // TODO(A5): follow NextPageToken. Until then any library above this many audio
    // files is silently truncated.
    private const int UnpaginatedPageSize = 100;

    public async Task<List<AudioFile>> GetAudioFilesAsync(CancellationToken cancellationToken = default)
    {
        var files = await ListAsync(DriveQuery.AllAudio(), UnpaginatedPageSize, cancellationToken);

        Log.DriveListed(logger, files.Count);

        return files.Select(ToAudioFile).ToList();
    }

    public async Task<List<AudioFile>> GetAudioFilesInFolderAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        // AudioInFolder validates the id rather than escaping it: the Drive id
        // alphabet excludes both the quote and the backslash, so anything containing
        // them is not an id and must be rejected outright.
        var files = await ListAsync(DriveQuery.AudioInFolder(folderId), UnpaginatedPageSize, cancellationToken);

        return files.Select(ToAudioFile).ToList();
    }

    public async Task<List<DriveFile>> SearchAudioFilesAsync(
        string? searchTerm = null,
        CancellationToken cancellationToken = default)
    {
        var files = await ListAsync(DriveQuery.AudioNamed(searchTerm), UnpaginatedPageSize, cancellationToken);

        return files.Select(ToDriveFile).ToList();
    }

    public async Task<List<DriveFile>> SearchFilesByNameAsync(
        string? fileName,
        CancellationToken cancellationToken = default)
    {
        var files = await ListAsync(DriveQuery.Named(fileName), 50, cancellationToken);

        return files.Select(ToDriveFile).ToList();
    }

    /// <summary>Metadata for a single file, or null when Drive reports it missing.</summary>
    /// <remarks>
    /// Callers needing one file's name or MIME type used to obtain it by listing the
    /// entire library and searching the result: 100 quota units per request instead of
    /// 5, and because that listing stopped at 100 files it made every track beyond the
    /// hundredth report "File not found" even though its bytes streamed perfectly.
    /// </remarks>
    public async Task<AudioFile?> GetAudioFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        var driveService = await DriveAsync(cancellationToken);

        var request = driveService.Files.Get(DriveQuery.RequireFileId(fileId));
        request.Fields = SingleFileFields;

        try
        {
            return ToAudioFile(await request.ExecuteAsync(cancellationToken));
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> DownloadAudioFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        var driveService = await DriveAsync(cancellationToken);
        var request = driveService.Files.Get(DriveQuery.RequireFileId(fileId));

        // TODO(A6): replace with a range-passthrough proxy. This pulls the entire file
        // into process memory before a single byte reaches the client, so
        // time-to-first-byte equals full download time and every seek re-downloads the
        // whole track.
        var buffer = new MemoryStream();
        await request.DownloadAsync(buffer, cancellationToken);
        buffer.Position = 0;

        return buffer;
    }

    private async Task<IList<DriveData.File>> ListAsync(
        string query,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var driveService = await DriveAsync(cancellationToken);

        var request = driveService.Files.List();
        request.Q = query;
        request.Fields = AudioFields;
        request.PageSize = pageSize;
        request.OrderBy = "name";

        var result = await request.ExecuteAsync(cancellationToken);

        return result.Files ?? [];
    }

    private static AudioFile ToAudioFile(DriveData.File file) => new()
    {
        Id = file.Id ?? string.Empty,
        Name = file.Name ?? string.Empty,
        MimeType = file.MimeType,
        Size = file.Size ?? 0,
        // The non-suffixed CreatedTime/ModifiedTime properties are obsolete and
        // documented as "may behave unexpectedly"; the DateTimeOffset variants are the
        // supported accessors.
        CreatedTime = file.CreatedTimeDateTimeOffset,
        ModifiedTime = file.ModifiedTimeDateTimeOffset,
        WebContentLink = file.WebContentLink,
        StreamUrl = $"/api/audiofiles/download/{file.Id}",
        FileExtension = FileExtensions.Resolve(file.Name, file.MimeType),
    };

    private static DriveFile ToDriveFile(DriveData.File file) => new()
    {
        Id = file.Id ?? string.Empty,
        Name = file.Name ?? string.Empty,
        MimeType = file.MimeType,
        Size = file.Size ?? 0,
        CreatedTime = file.CreatedTimeDateTimeOffset,
        ModifiedTime = file.ModifiedTimeDateTimeOffset,
        IsAudio = file.MimeType?.Contains("audio/", StringComparison.OrdinalIgnoreCase) == true,
        WebContentLink = file.WebContentLink,
    };
}
