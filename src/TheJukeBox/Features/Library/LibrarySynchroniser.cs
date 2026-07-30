using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TheJukeBox.API;
using TheJukeBox.Diagnostics;
using TheJukeBox.Domain;
using TheJukeBox.Infrastructure.Drive;
using TheJukeBox.Infrastructure.Metadata;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Library;

public sealed record SyncResult(int Added, int Updated, int Removed, int Unchanged)
{
    public int Total => Added + Updated + Unchanged;
}

/// <summary>
/// Reconciles a user's Drive audio files into the <see cref="Track"/> table.
/// </summary>
/// <remarks>
/// Idempotent by construction: everything is matched on <c>(UserId, DriveFileId)</c> and
/// only columns whose source actually changed are written. Running the same listing
/// twice must leave the table, and every RowVersion in it, untouched the second time,
/// because the sync is re-run on a timer and a version bump on every pass would make
/// clients re-download the library forever.
/// </remarks>
internal sealed class LibrarySynchroniser(
    JukeBoxDbContext context,
    IDriveLibraryClient driveClient,
    ILogger<LibrarySynchroniser> logger)
{
    public async Task<SyncResult> SynchroniseAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(userId, cancellationToken);

        state.Status = SyncStatus.Running;
        state.Phase = "Listing Drive";
        state.ItemsProcessed = 0;
        state.ItemsTotal = 0;
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            var result = await ReconcileAsync(userId, state, cancellationToken);

            state.Status = SyncStatus.Succeeded;
            state.Phase = null;
            state.LastFullSyncAtUtc = DateTimeOffset.UtcNow;
            state.LastIncrementalSyncAtUtc = state.LastFullSyncAtUtc;
            state.ConsecutiveFailures = 0;
            state.LastError = null;
            state.LastErrorType = null;

            await context.SaveChangesAsync(cancellationToken);

            Log.SyncCompleted(logger, userId, result.Added, result.Updated, result.Removed, result.Unchanged);

            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.SyncFailed(logger, userId, e);

            state.Status = SyncStatus.Failed;
            state.Phase = null;
            state.ConsecutiveFailures++;

            // The exception type, not its message: the message can carry request detail
            // and this value is surfaced to the client.
            state.LastErrorType = e.GetType().Name;
            state.LastError = e is Errors.JukeBoxException known ? known.Title : "The sync failed.";

            await context.SaveChangesAsync(CancellationToken.None);

            throw;
        }
    }

    private async Task<SyncResult> ReconcileAsync(
        Guid userId,
        DriveSyncState state,
        CancellationToken cancellationToken)
    {
        var discovered = new List<DriveAudioFile>();

        await foreach (var file in driveClient.EnumerateAudioAsync(userId, cancellationToken))
        {
            discovered.Add(file);
        }

        state.ItemsTotal = discovered.Count;
        state.Phase = "Reading names";
        await context.SaveChangesAsync(cancellationToken);

        // Resolved once per distinct folder rather than per file: a large library
        // typically lives in dozens of folders, and the folder name is the best album
        // signal available without reading tags.
        var folderNames = await driveClient.ResolveFolderNamesAsync(
            userId,
            discovered.Select(f => f.ParentFolderId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
            cancellationToken);

        var existing = await context.Tracks
            .Where(t => t.UserId == userId)
            .ToDictionaryAsync(t => t.DriveFileId, StringComparer.Ordinal, cancellationToken);

        var version = await NextVersionAsync(userId, cancellationToken);

        var added = 0;
        var updated = 0;
        var unchanged = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in discovered)
        {
            seen.Add(file.DriveFileId);

            folderNames.TryGetValue(file.ParentFolderId ?? string.Empty, out var folderName);

            if (existing.TryGetValue(file.DriveFileId, out var track))
            {
                if (Apply(track, file, folderName))
                {
                    track.RowVersion = ++version;
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }
            else
            {
                track = new Track
                {
                    UserId = userId,
                    DriveFileId = file.DriveFileId,
                    AddedAtUtc = DateTimeOffset.UtcNow,
                    RowVersion = ++version,
                };

                Apply(track, file, folderName);
                context.Tracks.Add(track);
                added++;
            }

            state.ItemsProcessed++;
        }

        var removed = 0;

        foreach (var (driveFileId, track) in existing)
        {
            if (seen.Contains(driveFileId) || track.DeletedAtUtc is not null)
            {
                continue;
            }

            // Soft delete, so playlist entries and history rows referencing a briefly
            // trashed file survive and un-trashing restores rather than duplicates.
            track.DeletedAtUtc = DateTimeOffset.UtcNow;
            track.RowVersion = ++version;
            removed++;
        }

        await context.SaveChangesAsync(cancellationToken);

        return new SyncResult(added, updated, removed, unchanged);
    }

    /// <summary>
    /// Copies Drive's view onto the track, returning whether anything actually changed.
    /// </summary>
    private static bool Apply(Track track, DriveAudioFile file, string? folderName)
    {
        // Identifies the bytes the metadata was read from. md5 when Drive gives one,
        // otherwise modified-time plus size, which is good enough to detect a real edit
        // while ignoring a pure rename.
        var fingerprint = file.Md5Checksum
                          ?? $"{file.ModifiedAtUtc:O}|{file.SizeBytes}";

        var extension = FileExtensions.Resolve(file.Name, file.MimeType);

        var metadataStale = track.MetadataSource == MetadataSource.None
                            || !string.Equals(track.MetadataFingerprint, fingerprint, StringComparison.Ordinal)
                            || !string.Equals(track.Name, file.Name, StringComparison.Ordinal)
                            || !string.Equals(track.ParentFolderName, folderName, StringComparison.Ordinal);

        var changed = track.Name != file.Name
                      || track.MimeType != file.MimeType
                      || track.SizeBytes != file.SizeBytes
                      || track.Md5Checksum != file.Md5Checksum
                      || track.HeadRevisionId != file.HeadRevisionId
                      || track.ParentFolderId != file.ParentFolderId
                      || track.ParentFolderName != folderName
                      || track.DriveModifiedAtUtc != file.ModifiedAtUtc
                      || track.FileExtension != extension
                      || track.DeletedAtUtc is not null
                      || metadataStale;

        if (!changed)
        {
            return false;
        }

        track.Name = file.Name;
        track.MimeType = file.MimeType;
        track.SizeBytes = file.SizeBytes;
        track.Md5Checksum = file.Md5Checksum;
        track.HeadRevisionId = file.HeadRevisionId;
        track.ParentFolderId = file.ParentFolderId;
        track.ParentFolderName = folderName;
        track.DriveCreatedAtUtc = file.CreatedAtUtc;
        track.DriveModifiedAtUtc = file.ModifiedAtUtc;
        track.FileExtension = extension;

        // A file reappearing after being trashed is un-deleted rather than duplicated.
        track.DeletedAtUtc = null;

        if (metadataStale && track.MetadataSource != MetadataSource.Tags)
        {
            // Tag-derived metadata is never overwritten by a filename guess. B1 sets
            // MetadataSource.Tags, and a rename must not undo it.
            var guessed = FilenameMetadataParser.Parse(file.Name, folderName);

            track.Title = guessed.Title;
            track.Artist = guessed.Artist;
            track.Album = guessed.Album;
            track.TrackNumber = guessed.TrackNumber;
            track.Year = guessed.Year;
            track.MetadataSource = MetadataSource.Filename;
            track.MetadataFingerprint = fingerprint;
            track.MetadataExtractedAtUtc = DateTimeOffset.UtcNow;
        }

        track.SearchText = BuildSearchText(track);

        return true;
    }

    /// <summary>
    /// Builds the normalised haystack used for substring search.
    /// </summary>
    /// <remarks>
    /// Accents are folded so "Bjork" finds "Björk" and "Sigur Ros" finds "Sigur Rós",
    /// which is what people actually type. Done here, once per change, rather than per
    /// query across every row.
    /// </remarks>
    private static string BuildSearchText(Track track)
    {
        var joined = string.Join(
            ' ',
            new[] { track.Title, track.Artist, track.Album, track.AlbumArtist, track.Name }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        var decomposed = joined.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToLowerInvariant(c));
            }
        }

        var result = folded.ToString().Normalize(NormalizationForm.FormC);

        return result.Length > 2048 ? result[..2048] : result;
    }

    private async Task<long> NextVersionAsync(Guid userId, CancellationToken cancellationToken) =>
        await context.Tracks
            .Where(t => t.UserId == userId)
            .MaxAsync(t => (long?)t.RowVersion, cancellationToken) ?? 0;

    private async Task<DriveSyncState> LoadStateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var state = await context.DriveSyncStates.FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

        if (state is not null)
        {
            return state;
        }

        state = new DriveSyncState { UserId = userId };
        context.DriveSyncStates.Add(state);

        return state;
    }
}
