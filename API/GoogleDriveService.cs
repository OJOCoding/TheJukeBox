using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TheJukeBox.API
{
    public interface IGoogleDriveService
    {
        Task<List<AudioFile>> GetAudioFilesAsync();
        Task<List<AudioFile>> GetAudioFilesInFolderAsync(string folderId);
        Task<System.IO.Stream> DownloadAudioFileAsync(string fileId);
        Task<List<DriveFile>> SearchFilesByNameAsync(string fileName);
        Task<List<DriveFile>> SearchFilesByExtensionAsync(string extension);
        Task<List<DriveFile>> SearchFilesByMimeTypeAsync(string mimeType);
        Task<List<DriveFile>> SearchAudioFilesAsync(string searchTerm = "");
    }

    public class GoogleDriveService : IGoogleDriveService
    {
        private readonly IGoogleAuthService _authService;

        public GoogleDriveService(IGoogleAuthService authService)
        {
            _authService = authService;
        }

        public async Task<List<AudioFile>> GetAudioFilesAsync()
        {
            var driveService = await _authService.GetValidDriveServiceAsync();

            var request = driveService.Files.List();

            request.Q = "mimeType contains 'audio/' and trashed=false";
            request.Fields = "files(id, name, mimeType, size, createdTime, modifiedTime, webContentLink)";
            request.PageSize = 100;
            request.OrderBy = "name";

            var result = await request.ExecuteAsync();
            var audioFiles = new List<AudioFile>();

            foreach (var file in result.Files)
            {
                var streamUrl = $"/api/audiofiles/download/{file.Id}";
                var cleanMimeType = CleanString(file.MimeType);

                audioFiles.Add(new AudioFile
                {
                    Id = file.Id,
                    Name = file.Name,
                    MimeType = file.MimeType,
                    Size = file.Size ?? 0,
                    CreatedTime = file.CreatedTime,
                    ModifiedTime = file.ModifiedTime,
                    StreamUrl = streamUrl,
                    FileExtension = GetFileExtension(file.Name, file.MimeType)
                });
            }

            return audioFiles;
        }
        private string CleanString(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // Remove any non-printable characters and trim
            return new string(input.Where(c => !char.IsControl(c)).ToArray()).Trim();
        }

        public async Task<List<AudioFile>> GetAudioFilesInFolderAsync(string folderId)
        {
            var driveService = await _authService.GetValidDriveServiceAsync();

            var request = driveService.Files.List();

            request.Q = $"'{folderId}' in parents and mimeType contains 'audio/' and trashed=false";
            request.Fields = "files(id, name, mimeType, size, createdTime, modifiedTime, webContentLink)";
            request.PageSize = 100;
            request.OrderBy = "name";

            var result = await request.ExecuteAsync();
            var audioFiles = new List<AudioFile>();

            foreach (var file in result.Files)
            {
                audioFiles.Add(new AudioFile
                {
                    Id = file.Id,
                    Name = file.Name,
                    MimeType = file.MimeType,
                    Size = file.Size ?? 0,
                    CreatedTime = file.CreatedTime,
                    ModifiedTime = file.ModifiedTime,
                    WebContentLink = file.WebContentLink,
                    FileExtension = GetFileExtension(file.Name, file.MimeType)
                });
            }

            return audioFiles;
        }

        public async Task<System.IO.Stream> DownloadAudioFileAsync(string fileId)
        {
            var driveService = await _authService.GetValidDriveServiceAsync();

            var request = driveService.Files.Get(fileId);
            var stream = new System.IO.MemoryStream();

            await request.DownloadAsync(stream);
            stream.Position = 0; 

            return stream;
        }

        public async Task<List<DriveFile>> SearchFilesByNameAsync(string fileName)
        {
            var driveService = await _authService.GetValidDriveServiceAsync();

            var request = driveService.Files.List();
            request.Q = $"name contains '{fileName}' and trashed=false";
            request.Fields = "files(id, name, mimeType, size, createdTime, modifiedTime)";
            request.PageSize = 50;

            var result = await request.ExecuteAsync();
            return ConvertToDriveFiles(result.Files);
        }

        public async Task<List<DriveFile>> SearchFilesByExtensionAsync(string extension)
        {
            var driveService = await _authService.GetValidDriveServiceAsync();

            var request = driveService.Files.List();
            request.Q = $"name contains '{extension}' and trashed=false";
            request.Fields = "files(id, name, mimeType, size, createdTime, modifiedTime)";
            request.PageSize = 50;

            var result = await request.ExecuteAsync();
            return ConvertToDriveFiles(result.Files);
        }

        public async Task<List<DriveFile>> SearchFilesByMimeTypeAsync(string mimeType)
        {
            var driveService = await _authService.GetValidDriveServiceAsync();

            var request = driveService.Files.List();
            request.Q = $"mimeType='{mimeType}' and trashed=false";
            request.Fields = "files(id, name, mimeType, size, createdTime, modifiedTime)";
            request.PageSize = 50;

            var result = await request.ExecuteAsync();
            return ConvertToDriveFiles(result.Files);
        }

        public async Task<List<DriveFile>> SearchAudioFilesAsync(string searchTerm = "")
        {
            var driveService = await _authService.GetValidDriveServiceAsync();

            string query;
            if (string.IsNullOrEmpty(searchTerm))
            {
                query = "mimeType contains 'audio/' and trashed=false";
            }
            else
            {
                query = $"name contains '{searchTerm}' and mimeType contains 'audio/' and trashed=false";
            }

            var request = driveService.Files.List();
            request.Q = query;
            request.Fields = "files(id, name, mimeType, size, createdTime, modifiedTime, webContentLink)";
            request.PageSize = 100;
            request.OrderBy = "name";

            var result = await request.ExecuteAsync();
            return ConvertToDriveFiles(result.Files);
        }

        private List<DriveFile> ConvertToDriveFiles(IList<Google.Apis.Drive.v3.Data.File> files)
        {
            var driveFiles = new List<DriveFile>();

            foreach (var file in files)
            {
                driveFiles.Add(new DriveFile
                {
                    Id = file.Id,
                    Name = file.Name,
                    MimeType = file.MimeType,
                    Size = file.Size ?? 0,
                    CreatedTime = file.CreatedTime,
                    ModifiedTime = file.ModifiedTime,
                    IsAudio = file.MimeType?.Contains("audio/") == true,
                    WebContentLink = file.WebContentLink
                });
            }

            return driveFiles;
        }

        private string GetFileExtension(string fileName, string mimeType)
        {
            // Try to get extension from filename first
            if (!string.IsNullOrEmpty(fileName) && fileName.Contains('.'))
            {
                var extension = Path.GetExtension(fileName).ToLower();
                // Validate it's a known audio extension
                var validExtensions = new[] { ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".mp4" };
                if (validExtensions.Contains(extension))
                {
                    return extension;
                }
            }

            // Fall back to MIME type mapping
            return mimeType?.ToLower() switch
            {
                "audio/mpeg" => ".mp3",
                "audio/wav" => ".wav",
                "audio/x-wav" => ".wav",
                "audio/ogg" => ".ogg",
                "audio/flac" => ".flac",
                "audio/x-flac" => ".flac",
                "audio/mp4" => ".m4a",
                "audio/x-m4a" => ".m4a",
                "audio/aac" => ".aac",
                _ => ".mp3" // Default to mp3 if unknown
            };
        }
    }

    public class AudioFile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string MimeType { get; set; }
        public long Size { get; set; }
        public DateTime? CreatedTime { get; set; }
        public DateTime? ModifiedTime { get; set; }
        public string StreamUrl { get; set; } 
        public string FileExtension { get; set; }

        // Helper properties
        public string FormattedSize => FormatFileSize(Size);
        public bool IsPlayable => IsSupportedAudioFormat(FileExtension);

        public string WebContentLink { get; internal set; }

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

        private bool IsSupportedAudioFormat(string extension)
        {
            var supportedFormats = new[] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac" };
            return supportedFormats.Contains(extension.ToLower());
        }
    }

    public class DriveFile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string MimeType { get; set; }
        public long Size { get; set; }
        public DateTime? CreatedTime { get; set; }
        public DateTime? ModifiedTime { get; set; }
        public bool IsAudio { get; set; }
        public string WebContentLink { get; internal set; }
    }
}