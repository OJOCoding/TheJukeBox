using Microsoft.AspNetCore.Mvc;
using TheJukeBox.Errors;

namespace TheJukeBox.API;

/// <summary>
/// Read-only access to the Drive audio library.
/// </summary>
/// <remarks>
/// Every action here previously wrapped itself in
/// <c>try { ... } catch (Exception ex) { return BadRequest(ex.Message); }</c>, which
/// leaked internal exception text to clients and reported authentication failures and
/// server faults alike as 400, making them indistinguishable to any central
/// client-side interceptor. Exceptions now propagate to a single
/// <see cref="ProblemDetailsExceptionHandler"/> that maps typed failures to correct
/// status codes and discloses nothing it should not.
/// <para>
/// NOTE(A4): these endpoints are still unauthenticated and operate on whichever single
/// Drive token this instance holds.
/// </para>
/// </remarks>
[ApiController]
[Route("api/audiofiles")]
public sealed class AudioFilesController(IGoogleDriveService driveService) : ControllerBase
{
    /// <summary>The whole audio library.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var files = await driveService.GetAudioFilesAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            count = files.Count,
            audio_files = files.Select(Describe),
        });
    }

    /// <summary>Audio files directly inside one folder.</summary>
    [HttpGet("folder/{folderId}")]
    public async Task<IActionResult> GetInFolder(string folderId, CancellationToken cancellationToken)
    {
        var files = await driveService.GetAudioFilesInFolderAsync(folderId, cancellationToken);

        return Ok(new
        {
            success = true,
            folder_id = folderId,
            count = files.Count,
            audio_files = files.Select(Describe),
        });
    }

    /// <summary>
    /// Searches the library by file name.
    /// </summary>
    /// <remarks>
    /// Replaces three overlapping actions (<c>search</c>, <c>search-audio</c> and
    /// <c>search-by-name</c>) that differed only in which fields they projected. None
    /// was reachable from the UI, which had no search input at all.
    /// </remarks>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken cancellationToken)
    {
        var files = await driveService.SearchAudioFilesAsync(q, cancellationToken);

        return Ok(new
        {
            success = true,
            query = q ?? string.Empty,
            count = files.Count,
            audio_files = files.Select(f => new
            {
                id = f.Id,
                name = f.Name,
                type = f.MimeType,
                size = f.FormattedSize,
                created = f.CreatedTime,
            }),
        });
    }

    /// <summary>Aggregate counts and sizes for the library.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var files = await driveService.GetAudioFilesAsync(cancellationToken);
        var totalBytes = files.Sum(f => f.Size);

        return Ok(new
        {
            success = true,
            stats = new
            {
                total_files = files.Count,
                total_size_bytes = totalBytes,
                total_size_formatted = ByteSize.Format(totalBytes),
                file_types = files
                    .GroupBy(f => f.FileExtension ?? "unknown")
                    .Select(g => new
                    {
                        extension = g.Key,
                        count = g.Count(),
                        total_size = g.Sum(f => f.Size),
                    })
                    .OrderByDescending(x => x.count),
                recently_added = files
                    .OrderByDescending(f => f.CreatedTime)
                    .Take(5)
                    .Select(f => new { f.Name, f.CreatedTime }),
            },
        });
    }

    /// <summary>Streams one audio file's bytes.</summary>
    [HttpGet("download/{fileId}")]
    public async Task<IActionResult> Download(string fileId, CancellationToken cancellationToken)
    {
        // A single metadata get, not a whole-library listing: 5 quota units instead of
        // 100, and it resolves files beyond the listing's 100-item cap, which
        // previously 404'd here despite streaming fine.
        var file = await driveService.GetAudioFileAsync(fileId, cancellationToken)
                   ?? throw new ResourceNotFoundException("No such audio file.");

        var stream = await driveService.DownloadAudioFileAsync(fileId, cancellationToken);

        // Indexer, not Add: IHeaderDictionary.Add throws ArgumentException when the key
        // is already present, and File(..., enableRangeProcessing: true) sets
        // Accept-Ranges itself (analyzer ASP0019).
        Response.Headers.AcceptRanges = "bytes";

        return File(stream, FileExtensions.ContentTypeFor(file.FileExtension), enableRangeProcessing: true);
    }

    private static object Describe(AudioFile file) => new
    {
        id = file.Id,
        name = file.Name,
        type = file.MimeType,
        size = file.FormattedSize,
        extension = file.FileExtension,
        created = file.CreatedTime,
        modified = file.ModifiedTime,
        is_playable = file.IsPlayable,
        stream_url = file.StreamUrl,
    };
}
