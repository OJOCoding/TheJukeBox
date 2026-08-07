using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheJukeBox.Configuration;
using TheJukeBox.Diagnostics;
using TheJukeBox.Domain;
using TheJukeBox.Infrastructure.Metadata;
using TheJukeBox.Infrastructure.Metadata.Online;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Library;

public sealed record OnlineEnrichmentResult(int Examined, int Matched, int ArtStored, int Filled);

/// <summary>
/// The third pass: asks an online database about what the files did not say.
/// </summary>
/// <remarks>
/// <para>
/// A library assembled from downloads is mostly files with no tags at all. The first pass names
/// them from their filenames and the second reads whatever tags exist; this one covers what is
/// left, which in practice means cover art — the thing a music library most visibly lacks without
/// it.
/// </para>
/// <para>
/// <b>It only ever fills gaps.</b> A value already present is never replaced, whatever the match
/// says. Tags are what a file's owner chose; a match is this application's guess about someone
/// else's music, and the two are not the same kind of fact. That rule is enforced in one place,
/// <see cref="Fill"/>, so it cannot be forgotten by a later caller.
/// </para>
/// <para>
/// <b>Every track is attempted at most once per retry window</b>, matched or not. The failures are
/// the ones that need remembering: at one request per second, re-querying every unmatched track on
/// every sync would spend the entire rate budget learning nothing.
/// </para>
/// </remarks>
internal sealed class OnlineMetadataEnricher(
    JukeBoxDbContext context,
    IMusicBrainzClient musicBrainz,
    IOptions<OnlineMetadataOptions> options,
    ILogger<OnlineMetadataEnricher> logger)
{
    /// <summary>Releases to try for a cover before giving up on a match.</summary>
    /// <remarks>
    /// A recording appears on many releases and only some have artwork uploaded. Trying a few is
    /// worth it; trying all of them would spend a minute of rate budget on one track.
    /// </remarks>
    private const int MaxReleasesToTry = 3;

    /// <summary>Matches the truncation used for embedded art, so the two share rows.</summary>
    private const int ArtHashLength = 32;

    public async Task<OnlineEnrichmentResult> EnrichAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            return new OnlineEnrichmentResult(0, 0, 0, 0);
        }

        var candidates = await FindCandidatesAsync(userId, settings, cancellationToken);

        if (candidates.Count == 0)
        {
            return new OnlineEnrichmentResult(0, 0, 0, 0);
        }

        Log.OnlinePassStarted(logger, userId, candidates.Count);

        var examined = 0;
        var matched = 0;
        var artStored = 0;
        var filled = 0;

        foreach (var track in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await LookUpAsync(track, settings, cancellationToken);

            // The service could not answer. Nothing was learned, so nothing is recorded and the
            // pass stops: continuing would be more requests into a closed door, and stamping the
            // track would blacklist it for a month over a momentary 503.
            if (outcome.Unavailable)
            {
                Log.OnlinePassInterrupted(logger, userId, examined);
                break;
            }

            examined++;

            // Stamped whatever the answer was, including a miss. This is the negative cache.
            track.OnlineLookupAtUtc = DateTimeOffset.UtcNow;

            if (outcome.Match is null)
            {
                var reason = outcome.Rejection.ToString();

                Log.OnlineNoMatch(logger, track.Title, reason);
                continue;
            }

            matched++;
            track.MusicBrainzRecordingId = outcome.Match.RecordingId;

            if (Fill(track, outcome.Match))
            {
                filled++;
            }

            if (outcome.Art is not null && await StoreArtAsync(track, outcome.Art, cancellationToken))
            {
                artStored++;
            }
        }

        // One save for the pass. Each track is a second of rate limiting apart, so batching buys
        // nothing here except a single transaction and one fewer thing to get wrong.
        await context.SaveChangesAsync(cancellationToken);

        var result = new OnlineEnrichmentResult(examined, matched, artStored, filled);

        Log.OnlinePassCompleted(logger, userId, result.Examined, result.Matched, result.ArtStored, result.Filled);

        return result;
    }

    /// <summary>
    /// The tracks worth asking about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds: those with no cover art, and those whose metadata is only a filename guess. A
    /// track with real tags and a real cover has nothing an online database can add, and asking
    /// about it would spend rate budget to confirm what the file already said.
    /// </para>
    /// <para>
    /// The retry window is what makes this terminate. Without it the same unmatched tracks are
    /// re-queried on every sync forever; with it they are retried occasionally, because the
    /// database does grow.
    /// </para>
    /// </remarks>
    private async Task<List<Track>> FindCandidatesAsync(
        Guid userId,
        OnlineMetadataOptions settings,
        CancellationToken cancellationToken)
    {
        var retryBefore = DateTimeOffset.UtcNow.AddDays(-settings.RetryAfterDays);

        return await context.Tracks
            .Where(t => t.UserId == userId
                        && t.DeletedAtUtc == null
                        && (t.ArtSha256 == null || t.MetadataSource != MetadataSource.Tags)
                        && (t.OnlineLookupAtUtc == null || t.OnlineLookupAtUtc < retryBefore))

            // Oldest attempt first, so a capped pass works through the backlog rather than
            // re-examining the same head of the list every time.
            .OrderBy(t => t.OnlineLookupAtUtc == null ? 0 : 1)
            .ThenBy(t => t.OnlineLookupAtUtc)
            .ThenBy(t => t.Id)
            .Take(settings.MaxTracksPerPass)
            .ToListAsync(cancellationToken);
    }

    private async Task<(RecordingCandidate? Match, MatchRejection Rejection, CoverImage? Art, bool Unavailable)> LookUpAsync(
        Track track,
        OnlineMetadataOptions settings,
        CancellationToken cancellationToken)
    {
        var query = new LookupQuery(track.Title, track.Artist, track.Album, track.DurationSeconds);

        var search = await musicBrainz.SearchAsync(query, cancellationToken);

        if (search.ServiceUnavailable)
        {
            return (null, MatchRejection.NoCandidates, null, true);
        }

        var result = RecordingMatcher.Choose(
            query,
            search.Candidates,
            settings.MinimumScore,
            settings.DurationToleranceSeconds);

        if (result.Match is null)
        {
            return (null, result.Rejection, null, false);
        }

        // Art is only fetched when the track has none. A file that carried its own cover keeps it:
        // it is the one its owner has, and a request per track is the expensive part of this pass.
        if (track.ArtSha256 is not null)
        {
            return (result.Match, MatchRejection.None, null, false);
        }

        foreach (var release in result.Match.Releases.Take(MaxReleasesToTry))
        {
            var cover = await musicBrainz.GetFrontCoverAsync(release.ReleaseId, cancellationToken);

            if (cover is not null)
            {
                return (result.Match, MatchRejection.None, cover, false);
            }
        }

        return (result.Match, MatchRejection.None, null, false);
    }

    /// <summary>
    /// Copies across only what the track is missing.
    /// </summary>
    /// <remarks>
    /// The single place the never-overwrite rule lives. Every field is guarded the same way, so
    /// adding one later cannot accidentally opt out of it.
    /// </remarks>
    private static bool Fill(Track track, RecordingCandidate match)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrWhiteSpace(match.Artist))
        {
            track.Artist = match.Artist;
            changed = true;
        }

        var release = match.Releases.Count > 0 ? match.Releases[0] : null;

        if (string.IsNullOrWhiteSpace(track.Album) && !string.IsNullOrWhiteSpace(release?.Title))
        {
            track.Album = release!.Title;
            changed = true;
        }

        if (track.Year is null && release?.Year is > 0)
        {
            track.Year = release.Year;
            changed = true;
        }

        // Duration comes from the recording only when the file's own length is unknown, which it
        // is for a format the tag reader could not parse.
        if (track.DurationSeconds is null or <= 0 && match.LengthSeconds is > 0)
        {
            track.DurationSeconds = match.LengthSeconds;
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        // Promoted only from a guess. A track whose tags were read stays MetadataSource.Tags even
        // when this filled a blank, because the tags are still where most of its metadata came
        // from and that is what the label is for.
        if (track.MetadataSource != MetadataSource.Tags)
        {
            track.MetadataSource = MetadataSource.Online;
        }

        track.SearchText = TrackSearchText.Build(track);

        return true;
    }

    /// <summary>Stores a downloaded cover, reusing the row if those exact bytes are already held.</summary>
    private async Task<bool> StoreArtAsync(Track track, CoverImage cover, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(cover.Bytes))[..ArtHashLength];

        // Content-addressed, so a cover downloaded once serves every track on the album and every
        // user who owns it. The same table the embedded art goes in, deliberately: the source of
        // the bytes does not change what they are.
        //
        // Local is checked as well as the database, and that is not belt and braces. This pass
        // saves once at the end, so two tracks from the same album both find nothing stored, both
        // Add, and the second throws on a duplicate key in the change tracker. Two tracks from one
        // album is the ordinary case for this feature, not an edge one.
        var known = context.AlbumArt.Local.Any(a => string.Equals(a.Sha256, hash, StringComparison.Ordinal))
                    || await context.AlbumArt.AnyAsync(a => a.Sha256 == hash, cancellationToken);

        if (!known)
        {
            context.AlbumArt.Add(new AlbumArt
            {
                Sha256 = hash,
                Bytes = cover.Bytes,
                MimeType = cover.MimeType,
                SizeBytes = cover.Bytes.Length,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        track.ArtSha256 = hash;

        return !known;
    }
}
