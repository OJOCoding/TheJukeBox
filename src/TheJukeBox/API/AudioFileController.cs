using Microsoft.AspNetCore.Mvc;

namespace TheJukeBox.API
{
    [ApiController]
    [Route("api/[controller]")]
    public class AudioFilesController : ControllerBase
    {
        private readonly IGoogleDriveService _driveService;

        public AudioFilesController(IGoogleDriveService driveService)
        {
            _driveService = driveService;
        }


        [HttpGet("folder/{folderId}")]
        public async Task<IActionResult> GetAudioFilesInFolder(string folderId)
        {
            try
            {
                var audioFiles = await _driveService.GetAudioFilesInFolderAsync(folderId);

                return Ok(new
                {
                    success = true,
                    folder_id = folderId,
                    count = audioFiles.Count,
                    audio_files = audioFiles.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        type = f.MimeType,
                        size = f.FormattedSize,
                        extension = f.FileExtension,
                        is_playable = f.IsPlayable
                    })
                });
            }
            catch (System.Exception ex)
            {
                return BadRequest(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        [HttpGet("download/{fileId}")]
        public async Task<IActionResult> DownloadAudioFile(string fileId)
        {
            try
            {
                var stream = await _driveService.DownloadAudioFileAsync(fileId);

                // Get file info to set proper headers
                var audioFiles = await _driveService.GetAudioFilesAsync();
                var fileInfo = audioFiles.FirstOrDefault(f => f.Id == fileId);

                if (fileInfo == null)
                {
                    return NotFound(new { success = false, error = "File not found" });
                }

                // Set appropriate content type for audio streaming
                var contentType = GetContentType(fileInfo.FileExtension);

                // Enable range requests for seeking in audio players
                Response.Headers.Add("Accept-Ranges", "bytes");

                // Return the file stream with proper content type
                return File(stream, contentType, enableRangeProcessing: true);
            }
            catch (System.Exception ex)
            {
                return BadRequest(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        private string GetContentType(string fileExtension)
        {
            return fileExtension?.ToLower() switch
            {
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".flac" => "audio/flac",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                ".mp4" => "audio/mp4",
                _ => "application/octet-stream"
            };
        }

        [HttpGet("search")]
        public async Task<IActionResult> SearchFiles(
        [FromQuery] string name = "",
        [FromQuery] string extension = "",
        [FromQuery] string type = "audio")
        {
            try
            {
                List<DriveFile> files;

                if (!string.IsNullOrEmpty(name))
                {
                    files = await _driveService.SearchFilesByNameAsync(name);
                }
                else if (!string.IsNullOrEmpty(extension))
                {
                    files = await _driveService.SearchFilesByExtensionAsync(extension);
                }
                else if (type == "audio")
                {
                    files = await _driveService.SearchAudioFilesAsync();
                }
                else
                {
                    files = await _driveService.SearchAudioFilesAsync(name);
                }

                var audioFiles = files.Where(f => f.IsAudio).ToList();
                var otherFiles = files.Where(f => !f.IsAudio).ToList();

                return Ok(new
                {
                    success = true,
                    search_type = !string.IsNullOrEmpty(name) ? "name" :
                                 !string.IsNullOrEmpty(extension) ? "extension" : "all",
                    search_term = !string.IsNullOrEmpty(name) ? name :
                                 !string.IsNullOrEmpty(extension) ? extension : "all files",
                    total_files = files.Count,
                    audio_files_count = audioFiles.Count,
                    other_files_count = otherFiles.Count,
                    audio_files = audioFiles.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        type = f.MimeType,
                        size = FormatFileSize(f.Size),
                        created = f.CreatedTime?.ToString("yyyy-MM-dd HH:mm:ss"),
                        download_url = f.WebContentLink
                    }),
                    other_files = otherFiles.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        type = f.MimeType,
                        size = FormatFileSize(f.Size)
                    })
                });
            }
            catch (System.Exception ex)
            {
                return BadRequest(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        [HttpGet("search-audio")]
        public async Task<IActionResult> SearchAudioFiles([FromQuery] string term = "")
        {
            try
            {
                var audioFiles = await _driveService.SearchAudioFilesAsync(term);

                return Ok(new
                {
                    success = true,
                    search_term = term,
                    count = audioFiles.Count,
                    audio_files = audioFiles.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        type = f.MimeType,
                        size = FormatFileSize(f.Size),
                        created = f.CreatedTime?.ToString("yyyy-MM-dd HH:mm:ss"),
                        download_url = f.WebContentLink
                    })
                });
            }
            catch (System.Exception ex)
            {
                return BadRequest(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        [HttpGet("search-by-name")]
        public async Task<IActionResult> SearchFilesByName([FromQuery] string name)
        {
            try
            {
                var files = await _driveService.SearchFilesByNameAsync(name);
                var audioFiles = files.Where(f => f.IsAudio).ToList();

                return Ok(new
                {
                    success = true,
                    search_name = name,
                    total_files = files.Count,
                    audio_files_count = audioFiles.Count,
                    audio_files = audioFiles.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        type = f.MimeType,
                        size = FormatFileSize(f.Size),
                        download_url = f.WebContentLink
                    }),
                    all_files = files.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        type = f.MimeType,
                        is_audio = f.IsAudio,
                        size = FormatFileSize(f.Size)
                    })
                });
            }
            catch (System.Exception ex)
            {
                return BadRequest(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double len = bytes;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        [HttpGet("stats")]
        public async Task<IActionResult> GetStats()
        {
            try
            {
                var audioFiles = await _driveService.GetAudioFilesAsync();

                var stats = new
                {
                    total_files = audioFiles.Count,
                    total_size_bytes = audioFiles.Sum(f => f.Size),
                    total_size_formatted = FormatTotalSize(audioFiles.Sum(f => f.Size)),
                    file_types = audioFiles.GroupBy(f => f.FileExtension)
                                          .Select(g => new
                                          {
                                              extension = g.Key,
                                              count = g.Count(),
                                              total_size = g.Sum(f => f.Size)
                                          })
                                          .OrderByDescending(x => x.count),
                    recently_added = audioFiles.OrderByDescending(f => f.CreatedTime)
                                              .Take(5)
                                              .Select(f => new { f.Name, f.CreatedTime })
                };

                return Ok(new
                {
                    success = true,
                    stats = stats
                });
            }
            catch (System.Exception ex)
            {
                return BadRequest(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        private string FormatTotalSize(long totalBytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double len = totalBytes;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        [HttpGet]
        public async Task<IActionResult> GetAllAudioFiles()
        {
            try
            {
                var audioFiles = await _driveService.GetAudioFilesAsync();

                return Ok(new
                {
                    success = true,
                    count = audioFiles.Count,
                    audio_files = audioFiles.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        type = f.MimeType,
                        size = f.FormattedSize,
                        extension = f.FileExtension,
                        created = f.CreatedTime?.ToString("yyyy-MM-dd HH:mm:ss"),
                        modified = f.ModifiedTime?.ToString("yyyy-MM-dd HH:mm:ss"),
                        is_playable = f.IsPlayable,
                        stream_url = f.StreamUrl
                    })
                });
            }
            catch (System.Exception ex)
            {
                return BadRequest(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }
    }
}