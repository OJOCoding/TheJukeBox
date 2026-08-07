using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TheJukeBox.Configuration;
using TheJukeBox.Diagnostics;

namespace TheJukeBox.Infrastructure.Metadata.Online;

/// <summary>Looks a recording up, and fetches its cover.</summary>
public interface IMusicBrainzClient
{
    /// <summary>Searches for recordings matching a file. Never throws for a per-track failure.</summary>
    Task<SearchOutcome> SearchAsync(LookupQuery query, CancellationToken cancellationToken);

    /// <summary>The front cover of a release, or null when there is none.</summary>
    Task<CoverImage?> GetFrontCoverAsync(string releaseId, CancellationToken cancellationToken);
}

/// <param name="MimeType">Sniffed from the bytes, never taken from the response header.</param>
public sealed record CoverImage(byte[] Bytes, string MimeType);

/// <summary>
/// The result of a search, distinguishing "nothing matched" from "could not ask".
/// </summary>
/// <remarks>
/// Those two are opposite facts and were being conflated. A search that came back empty is a
/// finding worth remembering; a search that never happened because the service was rate limiting
/// is not, and recording it as a miss blacklists the track for a month over a momentary 503.
/// </remarks>
public sealed record SearchOutcome(IReadOnlyList<RecordingCandidate> Candidates, bool ServiceUnavailable)
{
    public static SearchOutcome Nothing { get; } = new([], false);

    public static SearchOutcome Unavailable { get; } = new([], true);
}

/// <summary>
/// MusicBrainz and the Cover Art Archive.
/// </summary>
/// <remarks>
/// <para>
/// Both are free, need no key, and are run on donated infrastructure. The obligations that come
/// with that are real and are honoured here: an identifying User-Agent with a contact address, and
/// no more than one request per second across the whole process. A client that ignores either gets
/// blocked, and deservedly.
/// </para>
/// <para>
/// Nothing here throws for a per-track problem. A lookup is a nicety on top of a library that
/// already works, so a timeout, a 503 or a malformed payload has to degrade to "no match" rather
/// than fail anybody's sync.
/// </para>
/// </remarks>
internal sealed class MusicBrainzClient(
    HttpClient client,
    MusicBrainzRateLimiter rateLimiter,
    IOptions<OnlineMetadataOptions> options,
    ILogger<MusicBrainzClient> logger) : IMusicBrainzClient
{
    /// <summary>The client name this application identifies itself as.</summary>
    internal const string ProductName = "TheJukeBox";

    /// <summary>How many search results to consider. More would not improve a strict matcher.</summary>
    private const int SearchLimit = 5;

    /// <summary>Bounds a cover download, which is otherwise attacker-influenced in size.</summary>
    private const int MaxCoverBytes = 4 * 1024 * 1024;

    public async Task<SearchOutcome> SearchAsync(
        LookupQuery query,
        CancellationToken cancellationToken)
    {
        // Narrow first, then widen. A constrained query is more precise when the constraints are
        // true, and useless when they are not -- and one of them frequently is not: a downloaded
        // file's "album" tag is very often the folder it was ripped into. Searching for
        // recording:"Stairway to Heaven" AND release:"Darien Çepani 2-6" returns nothing, for a
        // recording with sixteen hundred entries in the database.
        //
        // Widening is safe because the matcher, not the query, decides what counts as a match: it
        // still demands the duration agree and the title and artist correspond. The query's job is
        // only to put the right recording somewhere in the shortlist.
        foreach (var lucene in QueryLadder(query))
        {
            var outcome = await SearchOnceAsync(lucene, cancellationToken);

            // Stop climbing the moment the service says it cannot answer. Widening the query would
            // just be another request into a closed door, and reporting "nothing found" would be a
            // lie the caller then caches for a month.
            if (outcome.ServiceUnavailable)
            {
                return outcome;
            }

            if (outcome.Candidates.Count > 0)
            {
                return outcome;
            }
        }

        return SearchOutcome.Nothing;
    }

    /// <summary>
    /// The queries to try, most specific first.
    /// </summary>
    /// <remarks>
    /// Deduplicated, so a track with no artist and no album costs one request rather than three.
    /// At one request per second that difference is the whole runtime of a pass.
    /// </remarks>
    internal static IEnumerable<string> QueryLadder(LookupQuery query)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in new[]
                 {
                     BuildQuery(query),
                     BuildQuery(query with { Album = null }),
                     BuildQuery(query with { Album = null, Artist = null }),
                 })
        {
            if (candidate.Length > 0 && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// One search, retried once if the service asks us to slow down.
    /// </summary>
    /// <remarks>
    /// A 503 from MusicBrainz means "too fast", not "no such recording". They return it under load
    /// even to a client inside the published rate, so a single retry after a pause converts most of
    /// them into answers -- and the ones it does not convert are reported as unavailable rather
    /// than as an absence of results.
    /// </remarks>
    private async Task<SearchOutcome> SearchOnceAsync(string lucene, CancellationToken cancellationToken)
    {
        var url = "https://musicbrainz.org/ws/2/recording"
                  + $"?query={Uri.EscapeDataString(lucene)}&fmt=json&limit={SearchLimit}";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await SendAsync(url, cancellationToken);

                if (IsBusy(response.StatusCode))
                {
                    if (attempt == 0)
                    {
                        await Task.Delay(BusyBackoff, cancellationToken);
                        continue;
                    }

                    Log.OnlineLookupUnavailable(logger, (int)response.StatusCode);

                    return SearchOutcome.Unavailable;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Log.OnlineLookupUnavailable(logger, (int)response.StatusCode);

                    // Any other error status is also "could not ask", not "nothing there".
                    return SearchOutcome.Unavailable;
                }

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

                return new SearchOutcome(ParseRecordings(json.RootElement), false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Cancellation of the sync itself propagates; everything else is a failed lookup.
                if (e is TaskCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                Log.OnlineLookupFailed(logger, e);

                return SearchOutcome.Unavailable;
            }
        }

        return SearchOutcome.Unavailable;
    }

    /// <summary>How long to wait before retrying a service that asked us to slow down.</summary>
    private static readonly TimeSpan BusyBackoff = TimeSpan.FromSeconds(3);

    private static bool IsBusy(HttpStatusCode status) =>
        status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;

    public async Task<CoverImage?> GetFrontCoverAsync(string releaseId, CancellationToken cancellationToken)
    {
        // 500px: large enough for the Now Playing view at any sensible window size, small enough
        // that a library's worth of them stays in the tens of megabytes.
        var url = $"https://coverartarchive.org/release/{Uri.EscapeDataString(releaseId)}/front-500";

        try
        {
            using var response = await SendAsync(url, cancellationToken);

            // 404 is the ordinary answer for a release nobody has uploaded art for, and is not
            // worth logging as a failure.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Log.OnlineLookupUnavailable(logger, (int)response.StatusCode);
                return null;
            }

            // Checked before reading, so an oversized body is refused rather than buffered.
            if (response.Content.Headers.ContentLength > MaxCoverBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (bytes.Length == 0 || bytes.Length > MaxCoverBytes)
            {
                return null;
            }

            // Sniffed, never trusted from Content-Type. This value ends up in a header this
            // application serves, and the bytes come from a third party.
            var mimeType = ImageFormats.Sniff(bytes);

            return mimeType is null ? null : new CoverImage(bytes, mimeType);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException
                                  || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log.OnlineLookupFailed(logger, e);

            return null;
        }
    }

    /// <summary>One request, after waiting for the rate limiter's turn.</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Required by MusicBrainz's terms, and enforced by them: a generic agent is blocked.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(ProductName, ProductVersion));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue($"( {options.Value.ContactEmail} )"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        await rateLimiter.WaitAsync(cancellationToken);

        return await client.SendAsync(request, cancellationToken);
    }

    private static string ProductVersion =>
        typeof(MusicBrainzClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// Builds the Lucene query MusicBrainz expects.
    /// </summary>
    /// <remarks>
    /// Values are quoted and their quotes and backslashes escaped. Lucene treats a great deal of
    /// punctuation as syntax, and track titles are full of it -- an unescaped one is at best a
    /// query that finds nothing and at worst a parse error for every track with a colon in its
    /// name.
    /// </remarks>
    internal static string BuildQuery(LookupQuery query)
    {
        var title = Quote(query.Title);

        if (title.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<string> { $"recording:{title}" };

        var artist = Quote(query.Artist);
        if (artist.Length > 0) parts.Add($"artist:{artist}");

        var album = Quote(query.Album);
        if (album.Length > 0) parts.Add($"release:{album}");

        return string.Join(" AND ", parts);
    }

    private static string Quote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Trim();

        return escaped.Length == 0 ? string.Empty : $"\"{escaped}\"";
    }

    /// <summary>
    /// Reads the search payload defensively.
    /// </summary>
    /// <remarks>
    /// Every field is optional as far as this is concerned. The shape is a third party's and can
    /// change; a missing property must produce a candidate this application declines to use, not
    /// an exception in the middle of a sync.
    /// </remarks>
    internal static IReadOnlyList<RecordingCandidate> ParseRecordings(JsonElement root)
    {
        if (!root.TryGetProperty("recordings", out var recordings)
            || recordings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<RecordingCandidate>();

        foreach (var recording in recordings.EnumerateArray())
        {
            var id = String(recording, "id");
            var title = String(recording, "title");

            if (id is null || title is null)
            {
                continue;
            }

            results.Add(new RecordingCandidate(
                id,
                title,
                FirstArtist(recording),
                Releases(recording),

                // Absent score means the service did not rank it; treated as zero so the
                // threshold rejects it rather than a default letting it through.
                Int(recording, "score") ?? 0,

                // MusicBrainz reports length in milliseconds.
                Int(recording, "length") is { } ms and > 0 ? (int)Math.Round(ms / 1000.0) : null));
        }

        return results;
    }

    private static string? FirstArtist(JsonElement recording)
    {
        if (!recording.TryGetProperty("artist-credit", out var credits)
            || credits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var credit in credits.EnumerateArray())
        {
            // The name on the credit, falling back to the artist's own name: a credit can be
            // "Artist feat. Other" where the artist entity is just "Artist".
            var name = String(credit, "name")
                       ?? (credit.TryGetProperty("artist", out var artist) ? String(artist, "name") : null);

            if (name is not null)
            {
                return name;
            }
        }

        return null;
    }

    private static List<CandidateRelease> Releases(JsonElement recording)
    {
        if (!recording.TryGetProperty("releases", out var releases)
            || releases.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<CandidateRelease>();

        foreach (var release in releases.EnumerateArray())
        {
            var id = String(release, "id");

            if (id is null)
            {
                continue;
            }

            results.Add(new CandidateRelease(id, String(release, "title"), Year(String(release, "date"))));
        }

        return results;
    }

    /// <summary>A release date is "2011", "2011-05" or "2011-05-17"; only the year is wanted.</summary>
    private static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), CultureInfo.InvariantCulture, out var year)
            ? year
            : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;
}
