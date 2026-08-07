using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheJukeBox.Configuration;
using TheJukeBox.Domain;
using TheJukeBox.Features.Library;
using TheJukeBox.Infrastructure.Metadata.Online;

namespace TheJukeBox.Tests;

/// <summary>
/// A MusicBrainz that answers from a script rather than the network.
/// </summary>
internal sealed class FakeMusicBrainzClient : IMusicBrainzClient
{
    public List<LookupQuery> Searches { get; } = [];

    public List<string> CoverRequests { get; } = [];

    public IReadOnlyList<RecordingCandidate> Candidates { get; set; } = [];

    public CoverImage? Cover { get; set; }

    /// <summary>Makes every search report the service as unable to answer.</summary>
    public bool Unavailable { get; set; }

    public Task<SearchOutcome> SearchAsync(LookupQuery query, CancellationToken cancellationToken)
    {
        Searches.Add(query);

        return Task.FromResult(Unavailable
            ? SearchOutcome.Unavailable
            : new SearchOutcome(Candidates, false));
    }

    public Task<CoverImage?> GetFrontCoverAsync(string releaseId, CancellationToken cancellationToken)
    {
        CoverRequests.Add(releaseId);
        return Task.FromResult(Cover);
    }
}

/// <summary>
/// The rules the online pass has to obey.
/// </summary>
/// <remarks>
/// Two of them matter more than the rest, and both are about restraint: a guess must never
/// overwrite what a file actually said, and a track must never be looked up twice in a row. The
/// first protects the user's own metadata; the second protects a rate budget of one request per
/// second from being spent re-asking questions that already came back empty.
/// </remarks>
public class OnlineMetadataEnricherTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    /// <summary>A one-pixel PNG, so the art path stores something a sniffer accepts.</summary>
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>
    /// A cover whose bytes are unique to one test.
    /// </summary>
    /// <remarks>
    /// Album art is content-addressed and deliberately not scoped to a user, so identical bytes in
    /// two tests are the *same row* -- and the second test to run then sees "already stored" and
    /// counts zero. The tests passed individually and failed together, which is the signature of
    /// exactly this. Trailing bytes are appended rather than the header altered, because the format
    /// sniffer reads the signature and a real PNG's payload varies anyway.
    /// </remarks>
    private static CoverImage UniqueCover() =>
        new([.. PngBytes, .. Guid.NewGuid().ToByteArray()], "image/png");

    private static RecordingCandidate Candidate(
        string title = "Paranoid Android",
        string? artist = "Radiohead",
        string? releaseTitle = "OK Computer",
        int? year = 1997,
        int? length = 383) =>
        new(
            "mbid-recording",
            title,
            artist,
            [new CandidateRelease("mbid-release", releaseTitle, year)],
            100,
            length);

    [Fact]
    public async Task Nothing_happens_when_the_operator_has_not_enabled_it()
    {
        var userId = await SeedAsync(new Track { Title = "Paranoid Android", DurationSeconds = 383 });
        var client = new FakeMusicBrainzClient { Candidates = [Candidate()] };

        var result = await RunAsync(userId, client, enabled: false);

        Assert.Equal(0, result.Examined);
        Assert.Empty(client.Searches);
    }

    /// <summary>
    /// The rule that protects the user's own metadata. A filled field is never replaced, however
    /// confident the match.
    /// </summary>
    [Fact]
    public async Task Existing_values_are_never_overwritten()
    {
        var userId = await SeedAsync(new Track
        {
            Title = "Paranoid Android",
            Artist = "The Artist As Tagged",
            Album = "The Album As Tagged",
            Year = 1985,
            DurationSeconds = 383,
            MetadataSource = MetadataSource.Tags,
            ArtSha256 = "existingarthash0000000000000000a",
        });

        var client = new FakeMusicBrainzClient { Candidates = [Candidate()], Cover = UniqueCover() };

        await RunAsync(userId, client);

        var track = await SingleAsync(userId);

        Assert.Equal("The Artist As Tagged", track.Artist);
        Assert.Equal("The Album As Tagged", track.Album);
        Assert.Equal(1985, track.Year);
        Assert.Equal("existingarthash0000000000000000a", track.ArtSha256);

        // And still labelled as tag-derived, because that is where its metadata came from.
        Assert.Equal(MetadataSource.Tags, track.MetadataSource);

        // No cover was fetched: the track already had one, and that request is the expensive part.
        Assert.Empty(client.CoverRequests);
    }

    [Fact]
    public async Task Missing_values_are_filled_in()
    {
        var userId = await SeedAsync(new Track
        {
            Title = "Paranoid Android",
            DurationSeconds = 383,
            MetadataSource = MetadataSource.Filename,
        });

        var client = new FakeMusicBrainzClient
        {
            Candidates = [Candidate()],
            Cover = UniqueCover(),
        };

        var result = await RunAsync(userId, client);

        var track = await SingleAsync(userId);

        Assert.Equal("Radiohead", track.Artist);
        Assert.Equal("OK Computer", track.Album);
        Assert.Equal(1997, track.Year);
        Assert.Equal(MetadataSource.Online, track.MetadataSource);
        Assert.Equal("mbid-recording", track.MusicBrainzRecordingId);
        Assert.NotNull(track.ArtSha256);

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Filled);
        Assert.Equal(1, result.ArtStored);
    }

    /// <summary>
    /// Search has to be findable by what was filled in, or a track gains an artist and stays
    /// unsearchable by it.
    /// </summary>
    [Fact]
    public async Task Filling_a_field_updates_the_search_text()
    {
        var userId = await SeedAsync(new Track
        {
            Title = "Paranoid Android",
            DurationSeconds = 383,
            SearchText = "paranoid android",
            MetadataSource = MetadataSource.Filename,
        });

        await RunAsync(userId, new FakeMusicBrainzClient { Candidates = [Candidate()] });

        var track = await SingleAsync(userId);

        Assert.Contains("radiohead", track.SearchText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative cache. Without it, every sync re-queries every unmatched track at one request
    /// per second, forever, and never gets further.
    /// </summary>
    [Fact]
    public async Task A_track_that_found_nothing_is_not_asked_about_again()
    {
        var userId = await SeedAsync(new Track { Title = "Something Obscure", MetadataSource = MetadataSource.Filename });

        var client = new FakeMusicBrainzClient { Candidates = [] };

        await RunAsync(userId, client);

        Assert.Single(client.Searches);
        Assert.NotNull((await SingleAsync(userId)).OnlineLookupAtUtc);

        // A second pass, immediately afterwards, must ask nothing.
        var second = new FakeMusicBrainzClient { Candidates = [Candidate()] };

        var result = await RunAsync(userId, second);

        Assert.Empty(second.Searches);
        Assert.Equal(0, result.Examined);
    }

    /// <summary>But it is a window, not a permanent verdict: the database grows.</summary>
    [Fact]
    public async Task An_old_attempt_is_retried()
    {
        var userId = await SeedAsync(new Track { Title = "Something Obscure", MetadataSource = MetadataSource.Filename });

        await using (var context = database.CreateContext())
        {
            var track = await context.Tracks.SingleAsync(t => t.UserId == userId);
            track.OnlineLookupAtUtc = DateTimeOffset.UtcNow.AddDays(-90);
            await context.SaveChangesAsync();
        }

        var client = new FakeMusicBrainzClient { Candidates = [Candidate(title: "Something Obscure")] };

        await RunAsync(userId, client);

        Assert.Single(client.Searches);
    }

    /// <summary>
    /// A fully tagged track with its own cover has nothing to gain, and asking would spend a second
    /// of the rate budget to confirm what the file already said.
    /// </summary>
    [Fact]
    public async Task A_fully_tagged_track_with_art_is_not_looked_up()
    {
        var userId = await SeedAsync(new Track
        {
            Title = "Paranoid Android",
            Artist = "Radiohead",
            MetadataSource = MetadataSource.Tags,
            ArtSha256 = "hash00000000000000000000000000ab",
        });

        var client = new FakeMusicBrainzClient { Candidates = [Candidate()] };

        var result = await RunAsync(userId, client);

        Assert.Empty(client.Searches);
        Assert.Equal(0, result.Examined);
    }

    /// <summary>A tagged track missing only its cover is still worth asking about.</summary>
    [Fact]
    public async Task A_tagged_track_without_art_is_looked_up_for_the_cover()
    {
        var userId = await SeedAsync(new Track
        {
            Title = "Paranoid Android",
            Artist = "Radiohead",
            Album = "OK Computer",
            DurationSeconds = 383,
            MetadataSource = MetadataSource.Tags,
        });

        var client = new FakeMusicBrainzClient
        {
            Candidates = [Candidate()],
            Cover = UniqueCover(),
        };

        await RunAsync(userId, client);

        var track = await SingleAsync(userId);

        Assert.NotNull(track.ArtSha256);
        Assert.Equal(["mbid-release"], client.CoverRequests);

        // Still tag-derived: a downloaded cover does not change where the metadata came from.
        Assert.Equal(MetadataSource.Tags, track.MetadataSource);
    }

    /// <summary>
    /// Downloaded covers go in the same content-addressed table as embedded ones, so an album
    /// shared by several tracks -- or several users -- stores one row.
    /// </summary>
    [Fact]
    public async Task The_same_cover_is_stored_once()
    {
        var userId = await SeedAsync(
            new Track { Title = "Track One", DurationSeconds = 383, MetadataSource = MetadataSource.Filename },
            new Track { Title = "Track Two", DurationSeconds = 383, MetadataSource = MetadataSource.Filename });

        var client = new FakeMusicBrainzClient
        {
            Candidates = [Candidate(title: "Track")],
            Cover = UniqueCover(),
        };

        var result = await RunAsync(userId, client);

        Assert.Equal(2, result.Matched);
        Assert.Equal(1, result.ArtStored);

        await using var verify = database.CreateContext();

        var tracks = await verify.Tracks.Where(t => t.UserId == userId).ToListAsync();

        Assert.All(tracks, t => Assert.NotNull(t.ArtSha256));
        Assert.Single(tracks.Select(t => t.ArtSha256).Distinct());
    }

    /// <summary>A match that yields no artwork is still a match, and must not be retried at once.</summary>
    [Fact]
    public async Task A_match_with_no_available_cover_is_still_recorded()
    {
        var userId = await SeedAsync(new Track
        {
            Title = "Paranoid Android",
            DurationSeconds = 383,
            MetadataSource = MetadataSource.Filename,
        });

        var client = new FakeMusicBrainzClient { Candidates = [Candidate()], Cover = null };

        var result = await RunAsync(userId, client);

        var track = await SingleAsync(userId);

        Assert.Equal(1, result.Matched);
        Assert.Equal(0, result.ArtStored);
        Assert.Null(track.ArtSha256);
        Assert.Equal("mbid-recording", track.MusicBrainzRecordingId);
    }

    /// <summary>
    /// A service that cannot answer must not be recorded as having answered "no".
    /// </summary>
    /// <remarks>
    /// MusicBrainz returns 503 under load, even to a client inside its published rate. Treating
    /// that as a miss would blacklist the track for a month over a moment's congestion -- and it
    /// really happened: a live pass logged three 503s and cached three false misses.
    /// </remarks>
    [Fact]
    public async Task A_service_that_cannot_answer_is_not_recorded_as_a_miss()
    {
        var userId = await SeedAsync(new Track { Title = "Paranoid Android", MetadataSource = MetadataSource.Filename });

        var result = await RunAsync(userId, new FakeMusicBrainzClient { Unavailable = true });

        Assert.Equal(0, result.Examined);
        Assert.Null((await SingleAsync(userId)).OnlineLookupAtUtc);
    }

    /// <summary>And the next pass tries again, rather than skipping it for a month.</summary>
    [Fact]
    public async Task A_track_skipped_for_being_unreachable_is_retried_immediately()
    {
        var userId = await SeedAsync(new Track
        {
            Title = "Paranoid Android",
            DurationSeconds = 383,
            MetadataSource = MetadataSource.Filename,
        });

        await RunAsync(userId, new FakeMusicBrainzClient { Unavailable = true });

        var working = new FakeMusicBrainzClient { Candidates = [Candidate()] };

        var result = await RunAsync(userId, working);

        Assert.Single(working.Searches);
        Assert.Equal(1, result.Matched);
    }

    /// <summary>
    /// The pass stops rather than working through the rest. Continuing would be dozens more
    /// requests into a service that has just said it is overloaded.
    /// </summary>
    [Fact]
    public async Task The_pass_stops_when_the_service_stops_answering()
    {
        var tracks = Enumerable.Range(0, 5)
            .Select(i => new Track { Title = $"Track {i}", MetadataSource = MetadataSource.Filename })
            .ToArray();

        var userId = await SeedAsync(tracks);

        var client = new FakeMusicBrainzClient { Unavailable = true };

        await RunAsync(userId, client);

        Assert.Single(client.Searches);
    }

    /// <summary>One pass must not run away with the rate budget on a large library.</summary>
    [Fact]
    public async Task A_pass_stops_at_the_configured_cap()
    {
        var tracks = Enumerable.Range(0, 6)
            .Select(i => new Track { Title = $"Track {i}", MetadataSource = MetadataSource.Filename })
            .ToArray();

        var userId = await SeedAsync(tracks);

        var client = new FakeMusicBrainzClient { Candidates = [] };

        var result = await RunAsync(userId, client, maxPerPass: 2);

        Assert.Equal(2, result.Examined);
        Assert.Equal(2, client.Searches.Count);
    }

    /// <summary>Another user's tracks are not this pass's business.</summary>
    [Fact]
    public async Task Only_the_requested_users_tracks_are_examined()
    {
        var mine = await SeedAsync(new Track { Title = "Mine", MetadataSource = MetadataSource.Filename });
        await SeedAsync(new Track { Title = "Theirs", MetadataSource = MetadataSource.Filename });

        var client = new FakeMusicBrainzClient { Candidates = [] };

        var result = await RunAsync(mine, client);

        Assert.Equal(1, result.Examined);
        Assert.Equal("Mine", Assert.Single(client.Searches).Title);
    }

    private async Task<OnlineEnrichmentResult> RunAsync(
        Guid userId,
        IMusicBrainzClient client,
        bool enabled = true,
        int maxPerPass = 200)
    {
        await using var context = database.CreateContext();

        var options = Options.Create(new OnlineMetadataOptions
        {
            Enabled = enabled,
            ContactEmail = "test@example.com",
            MaxTracksPerPass = maxPerPass,
        });

        var enricher = new OnlineMetadataEnricher(
            context,
            client,
            options,
            NullLogger<OnlineMetadataEnricher>.Instance);

        return await enricher.EnrichAsync(userId);
    }

    private async Task<Track> SingleAsync(Guid userId)
    {
        await using var context = database.CreateContext();

        return await context.Tracks.AsNoTracking().SingleAsync(t => t.UserId == userId);
    }

    private async Task<Guid> SeedAsync(params Track[] tracks)
    {
        await using var context = database.CreateContext();

        var user = new User
        {
            GoogleSubjectId = $"subject-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow,
            Status = UserStatus.Active,
        };

        context.Users.Add(user);

        foreach (var track in tracks)
        {
            track.UserId = user.Id;
            track.DriveFileId = $"drive-{Guid.NewGuid():N}";
            track.Name = $"{track.Title}.mp3";
            track.SearchText = track.SearchText.Length == 0 ? track.Title.ToLowerInvariant() : track.SearchText;
            track.AddedAtUtc = DateTimeOffset.UtcNow;

            context.Tracks.Add(track);
        }

        await context.SaveChangesAsync();

        return user.Id;
    }
}
