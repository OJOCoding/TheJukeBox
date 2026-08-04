using Microsoft.EntityFrameworkCore;
using TheJukeBox.Domain;
using TheJukeBox.Features.Library;

namespace TheJukeBox.Tests;

/// <summary>
/// The URL keys for albums and artists.
/// </summary>
/// <remarks>
/// Albums are not rows, so their identity is their name -- and names contain slashes, percent
/// signs, non-ASCII text and, occasionally, nothing at all. The codec is the whole of that
/// identity, so it gets the awkward inputs.
/// </remarks>
public class GroupKeyTests
{
    [Theory]
    [InlineData("Radiohead")]
    [InlineData("AC/DC")]
    [InlineData("Sigur Rós")]
    [InlineData("100% Silk")]
    [InlineData("a?b&c=d")]
    [InlineData("../../etc/passwd")]
    [InlineData("  leading and trailing  ")]
    [InlineData("日本語のアーティスト")]
    [InlineData("emoji 🎵 in a name")]
    public void An_artist_key_round_trips(string artist) =>
        Assert.Equal(artist, GroupKey.ReadArtist(GroupKey.ForArtist(artist)));

    [Theory]
    [InlineData("Radiohead", "OK Computer")]
    [InlineData("AC/DC", "Back in Black")]
    [InlineData("Various Artists", "Now That's What I Call Music! 42")]
    [InlineData("", "")]
    [InlineData("artist", "")]
    [InlineData("", "album")]
    public void An_album_key_round_trips(string artist, string album)
    {
        var read = GroupKey.ReadAlbum(GroupKey.ForAlbum(artist, album));

        Assert.NotNull(read);
        Assert.Equal(artist, read!.Value.AlbumArtist);
        Assert.Equal(album, read.Value.Album);
    }

    /// <summary>
    /// An untagged track has no artist and no album. Those groups still need to be reachable, or
    /// a library of untagged files shows an albums screen with a dead link on it.
    /// </summary>
    [Fact]
    public void A_null_artist_and_album_round_trip_as_empty()
    {
        Assert.Equal(string.Empty, GroupKey.ReadArtist(GroupKey.ForArtist(null)));

        var read = GroupKey.ReadAlbum(GroupKey.ForAlbum(null, null));

        Assert.NotNull(read);
        Assert.Equal(string.Empty, read!.Value.AlbumArtist);
        Assert.Equal(string.Empty, read.Value.Album);
    }

    /// <summary>
    /// The specific failure the kind marker exists to prevent: base64url of an empty string is an
    /// empty string, so without a marker the "no artist" group's URL would be <c>/artists/</c>
    /// with nothing in the segment. Neither router matches that, so the group would render with a
    /// dead link -- and on a freshly synced, untagged library that group is most of the music.
    /// </summary>
    [Fact]
    public void A_key_for_an_absent_name_is_not_empty()
    {
        Assert.NotEmpty(GroupKey.ForArtist(null));
        Assert.NotEmpty(GroupKey.ForArtist(string.Empty));
        Assert.NotEmpty(GroupKey.ForAlbum(null, null));
        Assert.NotEmpty(GroupKey.ForAlbum(string.Empty, string.Empty));
    }

    /// <summary>
    /// An album key must not read as an artist key either. Both directions matter, because both
    /// endpoints take a key from the URL and neither can trust which kind it was handed.
    /// </summary>
    [Fact]
    public void An_album_key_is_not_accepted_as_an_artist_key() =>
        Assert.Null(GroupKey.ReadArtist(GroupKey.ForAlbum("Queen", "Greatest Hits")));

    /// <summary>
    /// The key is a URL segment, so it is untrusted input. Malformed keys must be a 404, not an
    /// exception -- which is what returning null gets the caller.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!not-base64!!!")]
    [InlineData("////")]
    [InlineData("a")]
    public void A_malformed_artist_key_is_rejected(string? key) =>
        Assert.Null(GroupKey.ReadArtist(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!not-base64!!!")]
    public void A_malformed_album_key_is_rejected(string? key) =>
        Assert.Null(GroupKey.ReadAlbum(key));

    /// <summary>
    /// An artist key is a valid base64 string but has no separator, so it is not an album key.
    /// Accepting it would resolve to an album whose title happened to be the whole encoded value.
    /// </summary>
    [Fact]
    public void An_artist_key_is_not_accepted_as_an_album_key() =>
        Assert.Null(GroupKey.ReadAlbum(GroupKey.ForArtist("Radiohead")));

    /// <summary>
    /// Keys go in URLs, so they must not need escaping. Base64url's alphabet is exactly this set,
    /// and padding is dropped so nothing has to think about '='.
    /// </summary>
    [Theory]
    [InlineData("Radiohead")]
    [InlineData("AC/DC")]
    [InlineData("100% Silk")]
    [InlineData("日本語")]
    public void A_key_is_url_safe(string artist)
    {
        var key = GroupKey.ForArtist(artist);

        Assert.Equal(key, Uri.EscapeDataString(key));
        Assert.DoesNotContain('=', key);
        Assert.DoesNotContain('+', key);
        Assert.DoesNotContain('/', key);
    }

    /// <summary>
    /// Two albums of the same name by different artists are different records. A key built from
    /// the title alone would merge every "Greatest Hits" in the library into one.
    /// </summary>
    [Fact]
    public void The_same_album_title_by_different_artists_yields_different_keys() =>
        Assert.NotEqual(
            GroupKey.ForAlbum("Queen", "Greatest Hits"),
            GroupKey.ForAlbum("ABBA", "Greatest Hits"));

    /// <summary>
    /// The key is derived from the name, so it is the same on every request and after every sync.
    /// A random or sequential id would break bookmarks each time the library was relisted.
    /// </summary>
    [Fact]
    public void A_key_is_stable_across_calls() =>
        Assert.Equal(GroupKey.ForAlbum("Boards of Canada", "Geogaddi"),
            GroupKey.ForAlbum("Boards of Canada", "Geogaddi"));

    /// <summary>
    /// A separator inside a name would let one album's key parse as another's. It cannot be typed
    /// or stored in a tag, but the split must still not produce a wrong match.
    /// </summary>
    [Fact]
    public void A_separator_inside_a_name_does_not_alias_another_album()
    {
        // The separator, escaped rather than typed: a raw control character in source is
        // invisible in review and does not survive every encoding round trip.
        const char separator = '\u001f';

        var sneaky = GroupKey.ForAlbum("a", $"b{separator}c");
        var honest = GroupKey.ForAlbum("a", "b");

        Assert.NotEqual(honest, sneaky);

        var read = GroupKey.ReadAlbum(sneaky);

        // Split on the first separator, so the title keeps the rest. It does not silently become
        // a different album that exists.
        Assert.Equal("a", read!.Value.AlbumArtist);
        Assert.Equal($"b{separator}c", read.Value.Album);
    }
}

/// <summary>
/// The grouping itself, against the real schema.
/// </summary>
/// <remarks>
/// Run against SQLite rather than a list, because the aggregates are translated to SQL. A
/// LINQ-to-objects double would happily evaluate <c>Distinct().Count()</c> inside a group
/// projection that the provider cannot translate at all.
/// </remarks>
public class BrowseGroupingTests(SqliteDatabase database) : IClassFixture<SqliteDatabase>
{
    /// <summary>
    /// Albums group by album artist so a compilation stays whole. Grouping by track artist would
    /// split a 12-track compilation into 12 one-track albums.
    /// </summary>
    [Fact]
    public async Task An_album_groups_by_album_artist_not_track_artist()
    {
        var userId = await SeedAsync(
            ("Various Artists", "Guest One", "Compilation", 1, 100),
            ("Various Artists", "Guest Two", "Compilation", 2, 200),
            ("Various Artists", "Guest Three", "Compilation", 3, 300));

        await using var context = database.CreateContext();

        var albums = await context.Tracks
            .Where(t => t.UserId == userId && t.DeletedAtUtc == null)
            .GroupBy(t => new { Artist = t.AlbumArtist ?? t.Artist, t.Album })
            .Select(g => new { g.Key.Artist, g.Key.Album, Count = g.Count(), Duration = g.Sum(t => t.DurationSeconds ?? 0) })
            .ToListAsync();

        var album = Assert.Single(albums);

        Assert.Equal("Various Artists", album.Artist);
        Assert.Equal("Compilation", album.Album);
        Assert.Equal(3, album.Count);
        Assert.Equal(600, album.Duration);
    }

    /// <summary>
    /// The opposite choice for artists: the people on a compilation must each appear, or the
    /// artists screen files them all under "Various Artists" and hides them.
    /// </summary>
    [Fact]
    public async Task Artists_group_by_track_artist_so_compilation_performers_appear()
    {
        var userId = await SeedAsync(
            ("Various Artists", "Guest One", "Compilation", 1, 100),
            ("Various Artists", "Guest Two", "Compilation", 2, 200));

        await using var context = database.CreateContext();

        var artists = await context.Tracks
            .Where(t => t.UserId == userId && t.DeletedAtUtc == null)
            .GroupBy(t => t.Artist)
            .Select(g => new { Artist = g.Key, Count = g.Count(), Albums = g.Select(t => t.Album).Distinct().Count() })
            .OrderBy(g => g.Artist)
            .ToListAsync();

        Assert.Equal(2, artists.Count);
        Assert.Equal(["Guest One", "Guest Two"], artists.Select(a => a.Artist));
        Assert.All(artists, a => Assert.Equal(1, a.Albums));
    }

    /// <summary>
    /// A track with no album tag still has to be reachable. Excluding nulls would hide untagged
    /// files entirely, which for a freshly synced library is most of them.
    /// </summary>
    [Fact]
    public async Task Tracks_with_no_album_form_their_own_group()
    {
        var userId = await SeedAsync(
            (null, "Someone", null, null, 100),
            (null, "Someone", null, null, 150));

        await using var context = database.CreateContext();

        var groups = await context.Tracks
            .Where(t => t.UserId == userId && t.DeletedAtUtc == null)
            .GroupBy(t => new { Artist = t.AlbumArtist ?? t.Artist, t.Album })
            .Select(g => new { g.Key.Artist, g.Key.Album, Count = g.Count() })
            .ToListAsync();

        var group = Assert.Single(groups);

        Assert.Equal("Someone", group.Artist);
        Assert.Null(group.Album);
        Assert.Equal(2, group.Count);

        // And the key for that group survives a round trip, so the view's link works.
        var key = GroupKey.ForAlbum(group.Artist, group.Album);
        var read = GroupKey.ReadAlbum(key);

        Assert.Equal(string.Empty, read!.Value.Album);
    }

    [Fact]
    public async Task A_soft_deleted_track_is_excluded_from_its_album()
    {
        var userId = await SeedAsync(
            ("Band", "Band", "Record", 1, 100),
            ("Band", "Band", "Record", 2, 200));

        await using (var context = database.CreateContext())
        {
            var track = await context.Tracks
                .Where(t => t.UserId == userId && t.TrackNumber == 2)
                .SingleAsync();

            track.DeletedAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await using var verify = database.CreateContext();

        var album = await verify.Tracks
            .Where(t => t.UserId == userId && t.DeletedAtUtc == null)
            .GroupBy(t => new { Artist = t.AlbumArtist ?? t.Artist, t.Album })
            .Select(g => new { Count = g.Count(), Duration = g.Sum(t => t.DurationSeconds ?? 0) })
            .SingleAsync();

        Assert.Equal(1, album.Count);
        Assert.Equal(100, album.Duration);
    }

    /// <summary>
    /// Cover art is picked with Min rather than an ordered First, because SQLite has no ordered
    /// aggregate here. Min is at least deterministic, so the grid does not reshuffle its artwork
    /// between reloads -- which looks like a rendering bug when it happens.
    /// </summary>
    [Fact]
    public async Task Album_art_selection_is_deterministic()
    {
        var userId = await SeedAsync(
            ("Band", "Band", "Record", 1, 100),
            ("Band", "Band", "Record", 2, 200),
            ("Band", "Band", "Record", 3, 300));

        await using (var context = database.CreateContext())
        {
            var tracks = await context.Tracks
                .Where(t => t.UserId == userId)
                .OrderBy(t => t.TrackNumber)
                .ToListAsync();

            // Deliberately out of order relative to the track numbers.
            tracks[0].ArtSha256 = "ccc";
            tracks[1].ArtSha256 = "aaa";
            tracks[2].ArtSha256 = "bbb";

            await context.SaveChangesAsync();
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var context = database.CreateContext();

            var art = await context.Tracks
                .Where(t => t.UserId == userId && t.DeletedAtUtc == null)
                .GroupBy(t => new { Artist = t.AlbumArtist ?? t.Artist, t.Album })
                .Select(g => g.Min(t => t.ArtSha256))
                .SingleAsync();

            Assert.Equal("aaa", art);
        }
    }

    /// <summary>
    /// The projection uses Distinct().Count() inside a group, which is the part most likely to
    /// fail translation. If EF cannot translate it the query throws rather than silently
    /// evaluating client-side, so this asserts it reaches SQL and answers correctly.
    /// </summary>
    [Fact]
    public async Task An_artists_album_count_is_computed_in_sql()
    {
        var userId = await SeedAsync(
            ("Band", "Band", "First", 1, 100),
            ("Band", "Band", "First", 2, 100),
            ("Band", "Band", "Second", 1, 100));

        await using var context = database.CreateContext();

        var query = context.Tracks
            .Where(t => t.UserId == userId && t.DeletedAtUtc == null)
            .GroupBy(t => t.Artist)
            .Select(g => new { Albums = g.Select(t => t.Album).Distinct().Count(), Tracks = g.Count() });

        // The generated SQL, as evidence this is not being evaluated in memory.
        var sql = query.ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);

        var artist = await query.SingleAsync();

        Assert.Equal(2, artist.Albums);
        Assert.Equal(3, artist.Tracks);
    }

    /// <summary>Another user's tracks must not appear in these aggregates.</summary>
    [Fact]
    public async Task Aggregates_are_scoped_to_one_user()
    {
        var mine = await SeedAsync(("Band", "Band", "Record", 1, 100));
        await SeedAsync(("Band", "Band", "Record", 2, 200));

        await using var context = database.CreateContext();

        var album = await context.Tracks
            .Where(t => t.UserId == mine && t.DeletedAtUtc == null)
            .GroupBy(t => new { Artist = t.AlbumArtist ?? t.Artist, t.Album })
            .Select(g => new { Count = g.Count(), Duration = g.Sum(t => t.DurationSeconds ?? 0) })
            .SingleAsync();

        Assert.Equal(1, album.Count);
        Assert.Equal(100, album.Duration);
    }

    /// <summary>Seeds a user with tracks, described as (albumArtist, artist, album, trackNo, seconds).</summary>
    private async Task<Guid> SeedAsync(
        params (string? AlbumArtist, string? Artist, string? Album, int? TrackNumber, int? Seconds)[] tracks)
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

        foreach (var (albumArtist, artist, album, trackNumber, seconds) in tracks)
        {
            context.Tracks.Add(new Track
            {
                UserId = user.Id,
                DriveFileId = $"drive-{Guid.NewGuid():N}",
                Name = $"{artist} - {album} - {trackNumber}.mp3",
                Title = $"{album} track {trackNumber}",
                AlbumArtist = albumArtist,
                Artist = artist,
                Album = album,
                TrackNumber = trackNumber,
                DurationSeconds = seconds,
                SearchText = $"{artist} {album}".ToLowerInvariant(),
                AddedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await context.SaveChangesAsync();

        return user.Id;
    }
}
