using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TheJukeBox.Diagnostics;
using TheJukeBox.Domain;
using TheJukeBox.Features.Streaming;
using TheJukeBox.Infrastructure.Drive;
using TheJukeBox.Infrastructure.Metadata;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Library;

/// <summary>What one enrichment pass did.</summary>
public sealed record EnrichmentResult(
    int Examined,
    int Tagged,
    int Guessed,
    int ArtStored,
    int RangeRequests)
{
    /// <summary>Range requests per examined file. The number this phase exists to keep small.</summary>
    public double RequestsPerFile => Examined == 0 ? 0 : (double)RangeRequests / Examined;
}

/// <summary>
/// Reads embedded tags for tracks whose rows already exist.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second pass, not part of enumeration.</b> Listing a Drive library is minutes of
/// paginated metadata calls; reading tags out of every file is tens of minutes of ranged
/// reads. Interleaving them would mean the library is not browsable until the slow half
/// finishes. Running enumeration to completion first makes a five-thousand-track library
/// appear within seconds, with filename-guessed titles, and lets the good titles arrive
/// underneath it.
/// </para>
/// <para>
/// <b>The query is the work queue.</b> There is no job table, no cursor, and no resume
/// bookkeeping: "which tracks need tags" is a predicate over the same rows the UI reads. A
/// pass killed halfway leaves the rows it finished marked as done and the rest still
/// matching the predicate, so the next pass simply continues. That property is what makes
/// this free to interrupt, and it is why the predicate is written in terms of the content
/// fingerprint rather than a timestamp.
/// </para>
/// <para>
/// <b>Batching, and why RowVersion moves once per batch.</b> Writes are committed fifty
/// tracks at a time, and every track in a batch receives the same new
/// <see cref="Track.RowVersion"/>. Bumping per track would advance the library version
/// thousands of times during one sync, and since a client re-fetches on any version change,
/// that is thousands of invalidations of every client's cache to deliver work that arrives
/// in a steady stream anyway.
/// </para>
/// </remarks>
internal sealed class MetadataEnricher(
    JukeBoxDbContext context,
    IDriveClientFactory driveClientFactory,
    IHttpClientFactory httpClientFactory,
    ITagReader tagReader,
    ILogger<MetadataEnricher> logger)
{
    /// <summary>
    /// Tracks per transaction. Small enough that a crash loses little and SQLite's single
    /// writer is not held for long; large enough that the per-commit cost disappears.
    /// </summary>
    internal const int BatchSize = 50;

    /// <summary>
    /// Concurrent extractions. The work is network-bound, so some parallelism is most of
    /// the wall-clock win, but Drive rate-limits per user and SQLite takes one writer, so
    /// going wider buys latency variance and 403s rather than throughput.
    /// </summary>
    internal const int Parallelism = 4;

    /// <summary>
    /// Hex characters of the SHA-256 kept as the art key. See <see cref="AlbumArt.Sha256"/>.
    /// </summary>
    internal const int ArtHashLength = 32;

    public async Task<EnrichmentResult> EnrichAsync(
        Guid userId,
        DriveSyncState? state = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = await FindCandidatesAsync(userId, cancellationToken);

        if (candidates.Count == 0)
        {
            return new EnrichmentResult(0, 0, 0, 0, 0);
        }

        Log.MetadataPassStarted(logger, userId, candidates.Count);

        if (state is not null)
        {
            // The UI's progress chip reads these three, so they are reset here to describe
            // the tag pass rather than continuing to describe the finished listing pass.
            state.Phase = "Reading tags";
            state.ItemsProcessed = 0;
            state.ItemsTotal = candidates.Count;

            await context.SaveChangesAsync(cancellationToken);
        }

        // One token for the whole pass. GetAccessTokenAsync performs a refresh exchange with
        // Google on every call, so asking per file would add thousands of round trips.
        var accessToken = new DriveAccessToken(driveClientFactory, userId);

        // The client streaming already registered: no timeout, and a shared handler, so a
        // pass over thousands of files reuses connections instead of exhausting sockets.
        var httpClient = httpClientFactory.CreateClient(StreamingEndpoints.HttpClientName);

        var tagged = 0;
        var guessed = 0;
        var artStored = 0;
        var requests = 0;

        foreach (var batch in candidates.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extractions = new ConcurrentBag<Extraction>();

            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Parallelism,
                    CancellationToken = cancellationToken,
                },
                async (candidate, token) =>
                {
                    // Nothing in here touches the DbContext. EF contexts are not
                    // thread-safe, and the reason this loop is safe at all is that
                    // extraction is pure network plus parsing, with every write deferred to
                    // the single-threaded commit below.
                    extractions.Add(await ExtractAsync(candidate, httpClient, accessToken, token));
                });

            var applied = await CommitAsync(userId, [.. extractions], state, cancellationToken);

            tagged += applied.Tagged;
            guessed += applied.Guessed;
            artStored += applied.ArtStored;
            requests += applied.RangeRequests;
        }

        var result = new EnrichmentResult(candidates.Count, tagged, guessed, artStored, requests);

        Log.MetadataPassCompleted(
            logger,
            userId,
            result.Examined,
            result.Tagged,
            result.Guessed,
            result.ArtStored,
            result.RangeRequests);

        return result;
    }

    /// <summary>
    /// The work queue, expressed as a query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three clauses, and each earns its place:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <see cref="Track.MetadataExtractedAtUtc"/> is null. This column means exactly one
    /// thing: the tag pass has looked at these bytes. The reconciler therefore leaves it
    /// null when it writes a filename guess, and clears it when a file's content changes
    /// under existing tag-derived metadata.
    /// </item>
    /// <item>
    /// The fingerprint is missing, which can only happen for a row written before this
    /// column existed.
    /// </item>
    /// <item>
    /// The fingerprint disagrees with Drive's current MD5. Belt and braces: it catches a
    /// content change even if the clearing above were missed.
    /// </item>
    /// </list>
    /// <para>
    /// A plain <c>MetadataSource &lt;&gt; 'Tags'</c> clause was the obvious formulation and
    /// is wrong, because a genuinely untagged file can never satisfy it. Every sync would
    /// re-download the front of every untagged file forever, which for a library assembled
    /// from YouTube is most of it. "Examined at this fingerprint" is the condition that
    /// actually terminates.
    /// </para>
    /// </remarks>
    private IQueryable<Track> Pending(Guid userId) =>
        context.Tracks
            .AsNoTracking()
            .Where(t => t.UserId == userId
                        && t.DeletedAtUtc == null
                        && (t.MetadataExtractedAtUtc == null
                            || t.MetadataFingerprint == null
                            || (t.Md5Checksum != null && t.MetadataFingerprint != t.Md5Checksum)));

    private async Task<List<Candidate>> FindCandidatesAsync(Guid userId, CancellationToken cancellationToken) =>
        await Pending(userId)

            // Smallest first. Files under the whole-file threshold cost one request each,
            // so an interrupted pass has finished as many tracks as possible.
            .OrderBy(t => t.SizeBytes)
            .Select(t => new Candidate(
                t.Id,
                t.DriveFileId,
                t.Name,
                t.MimeType,
                t.SizeBytes))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// How many tracks this pass would read.
    /// </summary>
    /// <remarks>
    /// Exposed so <c>SyncEnricherHandoffTests</c> can assert what the tag pass would do next
    /// without needing Drive and HTTP doubles, and -- more importantly -- without restating the
    /// predicate. A restated copy is what let the reconciler and this class disagree about the
    /// column's meaning in the first place, with no test able to see it.
    /// </remarks>
    internal Task<int> CountPendingAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Pending(userId).CountAsync(cancellationToken);

    /// <summary>
    /// Reads one file. Never throws for a per-file problem.
    /// </summary>
    private async Task<Extraction> ExtractAsync(
        Candidate candidate,
        HttpClient httpClient,
        DriveAccessToken accessToken,
        CancellationToken cancellationToken)
    {
        if (candidate.SizeBytes <= 0)
        {
            return new Extraction(candidate, null, null, 0);
        }

        DriveRandomAccessStream? stream = null;

        try
        {
            var reader = new DriveRangeReader(httpClient, accessToken, candidate.DriveFileId);

            stream = await DriveRandomAccessStream.OpenAsync(reader, candidate.SizeBytes, cancellationToken);

            return tagReader.TryRead(stream, candidate.Name, candidate.MimeType, out var tags)
                ? new Extraction(candidate, tags, BuildArt(tags), stream.RequestCount)
                : new Extraction(candidate, null, null, stream.RequestCount);
        }
        catch (RangeBudgetExceededException)
        {
            // The interesting failure. A reader walking a malformed or oddly interleaved
            // container hit the cap, so this track falls back to its filename rather than
            // being allowed to stall the pass. Logged at Information because it is rare and
            // worth noticing, unlike an unparsable file.
            Log.MetadataBudgetExceeded(logger, candidate.Name, stream?.RequestCount ?? 0);

            return new Extraction(candidate, null, null, stream?.RequestCount ?? 0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // One bad file must never fail a sync. That includes a 404 for a file deleted
            // between enumeration and now, a transient Drive 5xx, and anything the parser
            // does on hostile bytes.
            Log.MetadataFileFailed(logger, candidate.Name, e);

            return new Extraction(candidate, null, null, stream?.RequestCount ?? 0);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>
    /// Hashes the original embedded bytes and validates that they really are an image.
    /// </summary>
    /// <remarks>
    /// Hashing happens here, on the bytes exactly as they were embedded and before anything
    /// could re-encode them. That ordering is the whole deduplication mechanism: a re-encode
    /// is not bit-reproducible, so hashing afterwards would give each of an album's twelve
    /// identical pictures a different key.
    /// </remarks>
    private static AlbumArt? BuildArt(TrackTags tags)
    {
        if (tags.Art is not { Bytes.Length: > 0 } art)
        {
            return null;
        }

        var mimeType = ImageFormats.Sniff(art.Bytes);

        if (mimeType is null)
        {
            return null;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(art.Bytes))[..ArtHashLength];

        return new AlbumArt
        {
            Sha256 = hash,
            Bytes = art.Bytes,
            MimeType = mimeType,
            SizeBytes = art.Bytes.Length,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Writes one batch in one transaction, with a single version bump for the whole batch.
    /// </summary>
    private async Task<EnrichmentResult> CommitAsync(
        Guid userId,
        Extraction[] extractions,
        DriveSyncState? state,
        CancellationToken cancellationToken)
    {
        var ids = extractions.Select(e => e.Candidate.TrackId).ToArray();

        var tracks = await context.Tracks
            .Where(t => t.UserId == userId && ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        var version = await NextVersionAsync(userId, cancellationToken) + 1;

        var newArt = extractions
            .Select(e => e.Art)
            .OfType<AlbumArt>()
            .GroupBy(a => a.Sha256, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        // Only the hashes not already stored. This is where an album collapses to one row:
        // twelve tracks produce twelve identical hashes, one of which is new.
        var known = await context.AlbumArt
            .Where(a => newArt.Select(n => n.Sha256).Contains(a.Sha256))
            .Select(a => a.Sha256)
            .ToListAsync(cancellationToken);

        var stored = 0;

        foreach (var art in newArt.Where(a => !known.Contains(a.Sha256, StringComparer.Ordinal)))
        {
            context.AlbumArt.Add(art);
            stored++;
        }

        var tagged = 0;
        var guessed = 0;
        var requests = 0;

        foreach (var extraction in extractions)
        {
            requests += extraction.RangeRequests;

            if (!tracks.TryGetValue(extraction.Candidate.TrackId, out var track))
            {
                // Deleted or reassigned between the candidate query and now. Skipped rather
                // than resurrected.
                continue;
            }

            if (Apply(track, extraction))
            {
                tagged++;
            }
            else
            {
                guessed++;
            }

            track.RowVersion = version;
        }

        if (state is not null)
        {
            state.ItemsProcessed += extractions.Length;
        }

        await context.SaveChangesAsync(cancellationToken);

        // The tracked entities for this batch are no longer needed. Without this the
        // context accumulates every track in the library across a long pass, and change
        // detection on the next batch walks all of them.
        foreach (var track in tracks.Values)
        {
            context.Entry(track).State = EntityState.Detached;
        }

        return new EnrichmentResult(extractions.Length, tagged, guessed, stored, requests);
    }

    /// <summary>
    /// Writes one extraction onto its track. Returns whether tags were the source.
    /// </summary>
    private static bool Apply(Track track, Extraction extraction)
    {
        // Parsed regardless of the outcome: it supplies the floor for every field the tag
        // omitted. A file tagged with only a title should still get its artist from
        // "Burial - Archangel.mp3" rather than showing nothing.
        var guessed = FilenameMetadataParser.Parse(track.Name, track.ParentFolderName);

        var fingerprint = TrackFingerprint.For(
            track.Md5Checksum,
            track.DriveModifiedAtUtc,
            track.SizeBytes);

        var succeeded = extraction.Tags is { HasAnything: true };

        if (extraction.Tags is { HasAnything: true } tags)
        {
            track.Title = tags.Title ?? guessed.Title;
            track.Artist = tags.Artist ?? guessed.Artist;
            track.Album = tags.Album ?? guessed.Album;
            track.AlbumArtist = tags.AlbumArtist;
            track.TrackNumber = tags.TrackNumber ?? guessed.TrackNumber;
            track.DiscNumber = tags.DiscNumber;
            track.Year = tags.Year ?? guessed.Year;
            track.Genre = tags.Genre;
            track.DurationSeconds = tags.DurationSeconds;
            track.ArtSha256 = extraction.Art?.Sha256;
            track.MetadataSource = MetadataSource.Tags;
        }
        else
        {
            // Unparsable, over budget, or genuinely untagged. The filename guess is the
            // answer, and it is written in full rather than left as empty strings so the
            // library still reads as music.
            track.Title = guessed.Title;
            track.Artist = guessed.Artist;
            track.Album = guessed.Album;
            track.TrackNumber = guessed.TrackNumber;
            track.Year = guessed.Year;
            track.MetadataSource = MetadataSource.Filename;
        }

        // Stamped in both cases. This is what stops an untagged file being re-read on every
        // sync for the rest of its life: it has been examined at this fingerprint, and only
        // a content change makes it a candidate again.
        track.MetadataFingerprint = fingerprint;
        track.MetadataExtractedAtUtc = DateTimeOffset.UtcNow;

        // Rebuilt last, after every field it reads has been written.
        track.SearchText = TrackSearchText.Build(track);

        return succeeded;
    }

    private async Task<long> NextVersionAsync(Guid userId, CancellationToken cancellationToken) =>
        await context.Tracks
            .Where(t => t.UserId == userId)
            .MaxAsync(t => (long?)t.RowVersion, cancellationToken) ?? 0;

    /// <summary>Everything the extraction phase needs, and nothing the writer needs.</summary>
    private sealed record Candidate(
        Guid TrackId,
        string DriveFileId,
        string Name,
        string? MimeType,
        long SizeBytes);

    private sealed record Extraction(
        Candidate Candidate,
        TrackTags? Tags,
        AlbumArt? Art,
        int RangeRequests);
}
