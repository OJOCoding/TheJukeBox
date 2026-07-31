using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TheJukeBox.Domain;
using TheJukeBox.Features.Library;
using TheJukeBox.Features.Streaming;
using TheJukeBox.Infrastructure.Drive;
using TheJukeBox.Infrastructure.Metadata;

namespace TheJukeBox.Tests;

/// <summary>
/// Serves file bytes over HTTP the way Drive's <c>alt=media</c> endpoint does, honouring
/// Range, and counts the requests.
/// </summary>
internal sealed class FakeDriveMediaHandler(Dictionary<string, byte[]> filesById) : HttpMessageHandler
{
    public int Requests { get; private set; }

    public long BytesServed { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests++;

        // The URL shape the reader builds: .../files/{id}?alt=media&...
        var path = request.RequestUri!.AbsolutePath;
        var fileId = path[(path.LastIndexOf('/') + 1)..];

        if (!filesById.TryGetValue(fileId, out var content))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var range = request.Headers.Range?.Ranges.FirstOrDefault();

        var from = (int)(range?.From ?? 0);
        var to = (int)Math.Min(range?.To ?? content.Length - 1, content.Length - 1);

        if (from >= content.Length)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        }

        var slice = content[from..(to + 1)];
        BytesServed += slice.Length;

        var response = new HttpResponseMessage(
            range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice),
        };

        if (range is not null)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
        }

        return Task.FromResult(response);
    }
}

internal sealed class StubDriveClientFactory : IDriveClientFactory
{
    public Task<Google.Apis.Drive.v3.DriveService> CreateAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The enricher only needs an access token.");

    public Task<string> GetAccessTokenAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult("fake-access-token");
}

internal sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>
/// The enrichment pass against a real migrated database.
/// </summary>
/// <remarks>
/// The last untested claim from B1. Reading a tag was proven in TagReaderTests; what was
/// not proven is what the enricher then writes, and in particular that a second pass over
/// an unchanged library is a no-op. That property is load-bearing rather than cosmetic: the
/// pass is re-run after every sync, and advancing <see cref="Track.RowVersion"/> on an
/// unchanged file would make every client re-download the whole library, forever.
/// </remarks>
public class MetadataEnricherTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    private const string Md5 = "0123456789abcdef0123456789abcdef";

    private static (MetadataEnricher Enricher, FakeDriveMediaHandler Handler) Build(
        Infrastructure.Persistence.JukeBoxDbContext context,
        Dictionary<string, byte[]> files)
    {
        var handler = new FakeDriveMediaHandler(files);

        var enricher = new MetadataEnricher(
            context,
            new StubDriveClientFactory(),
            new SingleClientFactory(handler),
            new AtlTagReader(NullLogger<AtlTagReader>.Instance),
            NullLogger<MetadataEnricher>.Instance);

        return (enricher, handler);
    }

    /// <summary>
    /// Seeds a user and a track in the state the reconciler leaves behind: filename-derived
    /// metadata, no extraction recorded yet.
    /// </summary>
    private async Task<(Guid UserId, Guid TrackId, string DriveFileId)> SeedAsync(
        byte[] content,
        string fileName = "03 - Some Artist - Some Title.flac",
        string mimeType = "audio/flac",
        string? md5 = Md5)
    {
        var userId = Guid.CreateVersion7();
        var trackId = Guid.CreateVersion7();
        var driveFileId = "file" + Guid.NewGuid().ToString("N")[..16];

        await using var context = database.CreateContext();

        context.Users.Add(new User
        {
            Id = userId,
            GoogleSubjectId = "subject-" + userId.ToString("N"),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow,
        });

        context.Tracks.Add(new Track
        {
            Id = trackId,
            UserId = userId,
            DriveFileId = driveFileId,
            Name = fileName,
            MimeType = mimeType,
            SizeBytes = content.Length,
            Md5Checksum = md5,
            FileExtension = Path.GetExtension(fileName),
            Title = Path.GetFileNameWithoutExtension(fileName),
            MetadataSource = MetadataSource.Filename,
            SearchText = fileName.ToLowerInvariant(),
            AddedAtUtc = DateTimeOffset.UtcNow,
            RowVersion = 1,
        });

        await context.SaveChangesAsync();

        return (userId, trackId, driveFileId);
    }

    [Fact]
    public async Task Writes_tag_metadata_over_the_filename_guess()
    {
        var content = AudioFixtures.Flac();
        var (userId, trackId, driveFileId) = await SeedAsync(content);

        await using (var context = database.CreateContext())
        {
            var (enricher, _) = Build(context, new Dictionary<string, byte[]> { [driveFileId] = content });

            var result = await enricher.EnrichAsync(userId);

            Assert.Equal(1, result.Examined);
            Assert.Equal(1, result.Tagged);
        }

        await using var verify = database.CreateContext();
        var track = await verify.Tracks.SingleAsync(t => t.Id == trackId);

        Assert.Equal(MetadataSource.Tags, track.MetadataSource);
        Assert.Equal(AudioFixtures.Title, track.Title);
        Assert.Equal(AudioFixtures.Artist, track.Artist);
        Assert.Equal(AudioFixtures.Album, track.Album);
        Assert.Equal(AudioFixtures.TrackNumber, track.TrackNumber);
        Assert.NotNull(track.DurationSeconds);
        Assert.InRange(track.DurationSeconds!.Value, AudioFixtures.DurationSeconds - 1, AudioFixtures.DurationSeconds + 1);
    }

    /// <summary>
    /// The terminating condition. The candidate query keys on "examined at this
    /// fingerprint" rather than "source is not Tags", precisely so an untagged file is not
    /// re-read on every sync forever.
    /// </summary>
    [Fact]
    public async Task A_second_pass_over_an_unchanged_library_does_nothing()
    {
        var content = AudioFixtures.Flac();
        var (userId, trackId, driveFileId) = await SeedAsync(content);
        var files = new Dictionary<string, byte[]> { [driveFileId] = content };

        long versionAfterFirst;

        await using (var context = database.CreateContext())
        {
            var (enricher, _) = Build(context, files);
            await enricher.EnrichAsync(userId);
        }

        await using (var context = database.CreateContext())
        {
            versionAfterFirst = (await context.Tracks.SingleAsync(t => t.Id == trackId)).RowVersion;
        }

        // Non-vacuity guard. The assertion below is that the version did NOT move on the
        // second pass, which proves nothing unless it moved on the first. Seeded at 1.
        Assert.True(
            versionAfterFirst > 1,
            $"the first pass left RowVersion at {versionAfterFirst}, so the second-pass check is vacuous");

        await using (var context = database.CreateContext())
        {
            var (enricher, handler) = Build(context, files);

            var second = await enricher.EnrichAsync(userId);

            // Nothing examined means nothing fetched: no candidate query hit, so no HTTP.
            Assert.Equal(0, second.Examined);
            Assert.Equal(0, handler.Requests);
        }

        await using var verify = database.CreateContext();
        var track = await verify.Tracks.SingleAsync(t => t.Id == trackId);

        // The claim that matters. A bump here would invalidate every client's cached library
        // on every sync, forever.
        Assert.Equal(versionAfterFirst, track.RowVersion);
    }

    [Fact]
    public async Task An_untagged_file_is_not_re_read_on_every_pass()
    {
        var content = AudioFixtures.Mp3Untagged();
        var (userId, _, driveFileId) = await SeedAsync(content, "unknown.mp3", "audio/mpeg");
        var files = new Dictionary<string, byte[]> { [driveFileId] = content };

        await using (var context = database.CreateContext())
        {
            var (enricher, _) = Build(context, files);
            var first = await enricher.EnrichAsync(userId);
            Assert.Equal(1, first.Examined);
        }

        await using var context2 = database.CreateContext();
        var (again, handler) = Build(context2, files);

        // A "MetadataSource <> Tags" query could never terminate for a file with no tags,
        // and would re-download the front of it on every sync for the life of the library.
        Assert.Equal(0, (await again.EnrichAsync(userId)).Examined);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Stores_cover_art_and_links_it_by_content_hash()
    {
        var content = AudioFixtures.Flac(withPicture: true);
        var (userId, trackId, driveFileId) = await SeedAsync(content);

        await using (var context = database.CreateContext())
        {
            var (enricher, _) = Build(context, new Dictionary<string, byte[]> { [driveFileId] = content });

            Assert.Equal(1, (await enricher.EnrichAsync(userId)).ArtStored);
        }

        await using var verify = database.CreateContext();

        var track = await verify.Tracks.SingleAsync(t => t.Id == trackId);
        Assert.NotNull(track.ArtSha256);

        var art = await verify.AlbumArt.SingleAsync(a => a.Sha256 == track.ArtSha256);
        Assert.NotEmpty(art.Bytes);
        Assert.Contains("image/", art.MimeType, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deduplication, which is the entire reason art is content-addressed. Two tracks from
    /// one album carry byte-identical pictures and must share a single stored row.
    /// </summary>
    [Fact]
    public async Task Two_tracks_sharing_a_picture_store_one_image()
    {
        var content = AudioFixtures.Flac(withPicture: true);

        var userId = Guid.CreateVersion7();
        var files = new Dictionary<string, byte[]>();

        await using (var context = database.CreateContext())
        {
            context.Users.Add(new User
            {
                Id = userId,
                GoogleSubjectId = "subject-" + userId.ToString("N"),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                LastSeenAtUtc = DateTimeOffset.UtcNow,
            });

            for (var i = 0; i < 2; i++)
            {
                var driveFileId = $"shared{i}{Guid.NewGuid():N}"[..20];
                files[driveFileId] = content;

                context.Tracks.Add(new Track
                {
                    UserId = userId,
                    DriveFileId = driveFileId,
                    Name = $"0{i + 1} Track.flac",
                    MimeType = "audio/flac",
                    SizeBytes = content.Length,

                    // Distinct fingerprints, so both are genuinely examined rather than one
                    // being skipped and the test passing for the wrong reason.
                    Md5Checksum = $"{i}123456789abcdef0123456789abcde",
                    Title = $"0{i + 1} Track",
                    MetadataSource = MetadataSource.Filename,
                    SearchText = "track",
                    AddedAtUtc = DateTimeOffset.UtcNow,
                    RowVersion = 1,
                });
            }

            await context.SaveChangesAsync();
        }

        await using (var context = database.CreateContext())
        {
            var (enricher, _) = Build(context, files);
            var result = await enricher.EnrichAsync(userId);

            Assert.Equal(2, result.Examined);
        }

        await using var verify = database.CreateContext();

        var hashes = await verify.Tracks
            .Where(t => t.UserId == userId)
            .Select(t => t.ArtSha256)
            .ToListAsync();

        Assert.Equal(2, hashes.Count);
        Assert.All(hashes, h => Assert.NotNull(h));
        Assert.Single(hashes.Distinct());

        // One row, not two, for two tracks.
        Assert.Equal(1, await verify.AlbumArt.CountAsync(a => a.Sha256 == hashes[0]));
    }

    /// <summary>
    /// One unreadable file must never fail a pass over thousands, and must not leave the
    /// row looking as though tags were read.
    /// </summary>
    [Fact]
    public async Task An_unreadable_file_falls_back_without_failing_the_pass()
    {
        var rubbish = new byte[8192];
        var (userId, trackId, driveFileId) = await SeedAsync(rubbish, "broken.mp3", "audio/mpeg");

        await using (var context = database.CreateContext())
        {
            var (enricher, _) = Build(context, new Dictionary<string, byte[]> { [driveFileId] = rubbish });

            var result = await enricher.EnrichAsync(userId);

            Assert.Equal(1, result.Examined);
            Assert.Equal(0, result.Tagged);
        }

        await using var verify = database.CreateContext();
        var track = await verify.Tracks.SingleAsync(t => t.Id == trackId);

        Assert.NotEqual(MetadataSource.Tags, track.MetadataSource);

        // Marked as examined regardless, or the pass would retry it forever.
        Assert.NotNull(track.MetadataExtractedAtUtc);
    }

    [Fact]
    public async Task A_missing_file_does_not_fail_the_pass()
    {
        var content = AudioFixtures.Flac();
        var (userId, _, _) = await SeedAsync(content);

        await using var context = database.CreateContext();

        // The handler answers 404 for every id: the file vanished between the listing and
        // this pass, which is an ordinary race rather than an error.
        var (enricher, _) = Build(context, []);

        var result = await enricher.EnrichAsync(userId);

        Assert.Equal(1, result.Examined);
        Assert.Equal(0, result.Tagged);
    }

    /// <summary>
    /// Enrichment must stay cheap per file. Asserted as a ratio so the bound holds as the
    /// corpus grows rather than only for one file.
    /// </summary>
    [Fact]
    public async Task Reads_each_file_in_a_handful_of_requests()
    {
        var content = AudioFixtures.Mp3WithId3v2(frames: 8192);
        var (userId, _, driveFileId) = await SeedAsync(content, "big.mp3", "audio/mpeg");

        await using var context = database.CreateContext();
        var (enricher, handler) = Build(context, new Dictionary<string, byte[]> { [driveFileId] = content });

        var result = await enricher.EnrichAsync(userId);

        Assert.Equal(1, result.Examined);

        Assert.True(
            result.RequestsPerFile <= DriveRandomAccessStream.MaxRequests,
            $"{result.RequestsPerFile} requests per file exceeds the budget");

        Assert.True(
            handler.BytesServed < content.Length,
            $"served {handler.BytesServed} of {content.Length} bytes: the file was downloaded");
    }
}
