using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
    IDriveChangesClient changesClient,
    ILogger<LibrarySynchroniser> logger)
{
    /// <summary>
    /// Brings a user's library up to date, incrementally when possible.
    /// </summary>
    /// <param name="forceFull">
    /// Discards the change token and relists everything. For the "rescan my Drive" button,
    /// which exists because no incremental feed is ever trusted absolutely.
    /// </param>
    public async Task<SyncResult> SynchroniseAsync(
        Guid userId,
        bool forceFull = false,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(userId, cancellationToken);

        var incremental = !forceFull && !string.IsNullOrEmpty(state.ChangesStartPageToken);

        state.Status = SyncStatus.Running;
        state.Phase = incremental ? "Checking for changes" : "Listing Drive";
        state.ItemsProcessed = 0;
        state.ItemsTotal = 0;
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            SyncResult result;

            if (incremental)
            {
                try
                {
                    result = await ReconcileChangesAsync(userId, state, cancellationToken);
                }
                catch (DriveChangeTokenExpiredException)
                {
                    // The one recovery path that must exist. Change tokens are long-lived but
                    // not eternal, and without this "automatic sync" works for months and then
                    // stops permanently with nothing to indicate why.
                    state.ChangesStartPageToken = null;
                    state.Phase = "Listing Drive";
                    await context.SaveChangesAsync(cancellationToken);

                    result = await ReconcileFullAsync(userId, state, cancellationToken);
                }
            }
            else
            {
                result = await ReconcileFullAsync(userId, state, cancellationToken);
            }

            state.Status = SyncStatus.Succeeded;
            state.Phase = null;
            state.LastIncrementalSyncAtUtc = DateTimeOffset.UtcNow;
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

    /// <summary>
    /// Applies Drive's change feed, which is the ordinary case once a library is established.
    /// </summary>
    /// <remarks>
    /// A full listing of a large library is thousands of files and hundreds of quota units
    /// every time, to discover that nothing moved. The change feed answers the same question
    /// in one request when nothing has happened, which is what makes running sync on a timer
    /// reasonable rather than abusive.
    /// </remarks>
    private async Task<SyncResult> ReconcileChangesAsync(
        Guid userId,
        DriveSyncState state,
        CancellationToken cancellationToken)
    {
        var existing = await context.Tracks
            .Where(t => t.UserId == userId)
            .ToDictionaryAsync(t => t.DriveFileId, StringComparer.Ordinal, cancellationToken);

        var version = await NextVersionAsync(userId, cancellationToken);

        var added = 0;
        var updated = 0;
        var removed = 0;
        var unchanged = 0;
        var pages = 0;
        var applied = 0;

        var pageToken = state.ChangesStartPageToken;

        while (!string.IsNullOrEmpty(pageToken))
        {
            var page = await changesClient.GetChangesAsync(userId, pageToken, cancellationToken);
            pages++;

            var folderIds = page.Changes
                .Select(c => c.File?.ParentFolderId)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var folderNames = folderIds.Length == 0
                ? []
                : await driveClient.ResolveFolderNamesAsync(userId, folderIds, cancellationToken);

            foreach (var change in page.Changes)
            {
                applied++;

                if (change.IsRemoval)
                {
                    if (existing.TryGetValue(change.DriveFileId, out var doomed)
                        && doomed.DeletedAtUtc is null)
                    {
                        doomed.DeletedAtUtc = DateTimeOffset.UtcNow;
                        doomed.RowVersion = ++version;
                        removed++;
                    }

                    // No match is not an error: the change may concern a file we never
                    // tracked, which is most of them in a Drive that holds more than music.
                    continue;
                }

                folderNames.TryGetValue(change.File!.ParentFolderId ?? string.Empty, out var folderName);

                if (existing.TryGetValue(change.DriveFileId, out var track))
                {
                    if (Apply(track, change.File, folderName))
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
                        DriveFileId = change.DriveFileId,
                        AddedAtUtc = DateTimeOffset.UtcNow,
                        RowVersion = ++version,
                    };

                    Apply(track, change.File, folderName);
                    context.Tracks.Add(track);
                    existing[change.DriveFileId] = track;
                    added++;
                }
            }

            state.ItemsProcessed = applied;
            state.ItemsTotal = applied;

            // The token advances in the same transaction as the page it describes, so a crash
            // mid-feed resumes exactly where it stopped rather than replaying from the start
            // or, worse, skipping a page. newStartPageToken appears only on the final page.
            state.ChangesStartPageToken = page.NewStartPageToken ?? page.NextPageToken ?? pageToken;

            await context.SaveChangesAsync(cancellationToken);

            pageToken = page.NextPageToken;
        }

        Log.ChangesApplied(logger, userId, applied, pages);

        return new SyncResult(added, updated, removed, unchanged);
    }

    private async Task<SyncResult> ReconcileFullAsync(
        Guid userId,
        DriveSyncState state,
        CancellationToken cancellationToken)
    {
        // Captured BEFORE the listing, deliberately. A token taken afterwards would miss
        // everything that changed while the listing ran; taken first, those changes are
        // simply replayed on the next pass, and replaying is free because the reconcile is
        // idempotent.
        var startPageToken = await TryGetStartPageTokenAsync(userId, cancellationToken);

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

        // Only now, once the listing has been persisted, does the token become valid to
        // resume from. Storing it earlier would mean a failed listing left a token implying
        // work that never happened.
        state.ChangesStartPageToken = startPageToken;
        state.LastFullSyncAtUtc = DateTimeOffset.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        return new SyncResult(added, updated, removed, unchanged);
    }

    /// <summary>
    /// Obtains a change token, or null if Drive will not give one.
    /// </summary>
    /// <remarks>
    /// Never fatal. Failing to get a token costs the next sync its shortcut, which is a
    /// performance loss; failing the whole sync over it would cost the user their library.
    /// </remarks>
    private async Task<string?> TryGetStartPageTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            return await changesClient.GetStartPageTokenAsync(userId, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.ChangeTokenExpired(logger, userId);
            return null;
        }
    }

    /// <summary>
    /// Copies Drive's view onto the track, returning whether anything actually changed.
    /// </summary>
    private static bool Apply(Track track, DriveAudioFile file, string? folderName)
    {
        // Identifies the bytes the *tag pass* read. Computed here only to notice that they have
        // changed; the value itself is written by MetadataEnricher and never by this method.
        // Shared with TrackFingerprint rather than recomputed, because two copies of the formula
        // are two things to keep in step.
        var fingerprint = TrackFingerprint.For(file.Md5Checksum, file.ModifiedAtUtc, file.SizeBytes);

        var extension = AudioFormats.ResolveExtension(file.Name, file.MimeType);

        // The bytes changed underneath metadata the tag pass had already examined, so whatever it
        // read is stale and the file has to go back to it.
        var contentChanged = track.MetadataExtractedAtUtc is not null
                             && track.MetadataFingerprint is not null
                             && !string.Equals(track.MetadataFingerprint, fingerprint, StringComparison.Ordinal);

        // A filename guess is derived from the file name and its folder, so only those can make it
        // stale. It deliberately does not consider the fingerprint: this pass never reads bytes, so
        // a content change tells it nothing new about what the name says.
        var guessStale = track.MetadataSource == MetadataSource.None
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
                      || guessStale
                      || contentChanged;

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

        if (contentChanged)
        {
            // Hand the file back to the tag pass. Clearing the timestamp is the whole mechanism:
            // it is the column the enricher selects on, and "null" is how this pass says "these
            // bytes have never been examined". The old values are left in place so the row keeps
            // showing a real title until the new read replaces it.
            track.MetadataExtractedAtUtc = null;
        }

        if (guessStale && track.MetadataSource != MetadataSource.Tags)
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

            // MetadataFingerprint and MetadataExtractedAtUtc are deliberately NOT set here. They
            // mean "the tag pass has examined these bytes", and this pass has not: it has only
            // read the file's name. Setting them was a real bug -- it made every freshly synced
            // track look already-examined, so the tag pass found nothing to do and no library ever
            // got a duration or a cover image. See MetadataEnricher.Pending and
            // SyncEnricherHandoffTests.
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
