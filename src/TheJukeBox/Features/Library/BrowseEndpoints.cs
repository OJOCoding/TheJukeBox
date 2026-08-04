using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TheJukeBox.Domain;
using TheJukeBox.Features.Auth;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Library;

/// <summary>
/// Browsing the library by album and by artist.
/// </summary>
/// <remarks>
/// <para>
/// Aggregated in SQL, not in the application. Loading a library to group it in memory would make
/// the albums screen slower with every file added, and the indexes to answer these queries
/// already exist for the corresponding browse sorts.
/// </para>
/// <para>
/// Two grouping decisions worth stating, because they are the difference between a useful screen
/// and an irritating one:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Albums group by album artist, falling back to the track artist.</b> That is what keeps a
/// compilation or a guest-heavy record together as one album instead of scattering it into one
/// album per featured performer.
/// </item>
/// <item>
/// <b>Artists group by track artist.</b> The opposite choice, deliberately: grouping the artist
/// list by album artist would file every compilation under "Various Artists" and hide the people
/// actually on it.
/// </item>
/// </list>
/// </remarks>
public static class BrowseEndpoints
{
    /// <summary>
    /// The most groups one request returns.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than paging. A library large enough to exceed this has other problems, and
    /// the response reports the true total so the client can say the list is truncated instead of
    /// quietly showing part of it.
    /// </remarks>
    private const int MaxGroups = 1000;

    public static void MapBrowseEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api").RequireAuthorization().WithTags("Browse");

        group.MapGet("/albums", GetAlbumsAsync);
        group.MapGet("/albums/{key}", GetAlbumAsync);

        group.MapGet("/artists", GetArtistsAsync);
        group.MapGet("/artists/{key}", GetArtistAsync);
    }

    private static async Task<Ok<GroupPageDto<AlbumDto>>> GetAlbumsAsync(
        ICurrentUser currentUser,
        JukeBoxDbContext database,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireId();

        var grouped = await Available(database, userId)

            // The album artist with a track-artist fallback, computed in the query so the
            // grouping and the label cannot disagree.
            .GroupBy(t => new { Artist = t.AlbumArtist ?? t.Artist, t.Album })
            .Select(g => new
            {
                g.Key.Artist,
                g.Key.Album,
                TrackCount = g.Count(),
                DurationSeconds = g.Sum(t => t.DurationSeconds ?? 0),

                // Min, not First: SQLite has no ordered aggregate here, and any cover from the
                // album is as good as any other. Min is at least deterministic, so the grid does
                // not shuffle its artwork between reloads.
                ArtSha256 = g.Min(t => t.ArtSha256),
                Year = g.Min(t => t.Year),
                PlayCount = g.Sum(t => t.PlayCount),
            })
            .OrderBy(g => g.Artist)
            .ThenBy(g => g.Album)
            .Take(MaxGroups + 1)
            .ToListAsync(cancellationToken);

        var truncated = grouped.Count > MaxGroups;

        var albums = grouped
            .Take(MaxGroups)
            .Select(g => new AlbumDto(
                GroupKey.ForAlbum(g.Artist, g.Album),
                g.Album,
                g.Artist,
                g.TrackCount,
                g.DurationSeconds,
                g.ArtSha256,
                g.Year,
                g.PlayCount))
            .ToList();

        return TypedResults.Ok(new GroupPageDto<AlbumDto>(albums.Count, truncated, albums));
    }

    private static async Task<Results<Ok<AlbumDetailDto>, NotFound>> GetAlbumAsync(
        string key,
        ICurrentUser currentUser,
        JukeBoxDbContext database,
        CancellationToken cancellationToken)
    {
        if (GroupKey.ReadAlbum(key) is not { } parts)
        {
            return TypedResults.NotFound();
        }

        // Empty string and null are the same album here: a track with no album tag is grouped as
        // "no album", and the key round-trips that as an empty string.
        var artist = Blank(parts.AlbumArtist);
        var album = Blank(parts.Album);

        var tracks = await Available(database, currentUser.RequireId())
            .Where(t => (t.AlbumArtist ?? t.Artist) == artist && t.Album == album)

            // Disc then track number, with a title tiebreak so untagged files keep a stable
            // order rather than swapping places between requests.
            .OrderBy(t => t.DiscNumber ?? 1)
            .ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.Title)
            .Select(t => new BrowseTrackDto(
                t.Id,
                t.Title,
                t.Artist,
                t.Album,
                t.TrackNumber,
                t.DiscNumber,
                t.DurationSeconds,
                t.ArtSha256,
                t.PlayCount))
            .ToListAsync(cancellationToken);

        if (tracks.Count == 0)
        {
            // The album may have existed when the client rendered its list and been trashed
            // since. Not found is the honest answer, and the view offers a way back.
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new AlbumDetailDto(
            key,
            album,
            artist,
            tracks.Sum(t => t.DurationSeconds ?? 0),
            tracks.Select(t => t.ArtSha256).FirstOrDefault(sha => sha is not null),
            tracks));
    }

    private static async Task<Ok<GroupPageDto<ArtistDto>>> GetArtistsAsync(
        ICurrentUser currentUser,
        JukeBoxDbContext database,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireId();

        var grouped = await Available(database, userId)
            .GroupBy(t => t.Artist)
            .Select(g => new
            {
                Artist = g.Key,
                TrackCount = g.Count(),
                DurationSeconds = g.Sum(t => t.DurationSeconds ?? 0),

                // Distinct album titles for this artist. Counted in SQL, because doing it in the
                // application means loading every track of every artist to count strings.
                AlbumCount = g.Select(t => t.Album).Distinct().Count(),
                ArtSha256 = g.Min(t => t.ArtSha256),
                PlayCount = g.Sum(t => t.PlayCount),
            })
            .OrderBy(g => g.Artist)
            .Take(MaxGroups + 1)
            .ToListAsync(cancellationToken);

        var truncated = grouped.Count > MaxGroups;

        var artists = grouped
            .Take(MaxGroups)
            .Select(g => new ArtistDto(
                GroupKey.ForArtist(g.Artist),
                g.Artist,
                g.AlbumCount,
                g.TrackCount,
                g.DurationSeconds,
                g.ArtSha256,
                g.PlayCount))
            .ToList();

        return TypedResults.Ok(new GroupPageDto<ArtistDto>(artists.Count, truncated, artists));
    }

    private static async Task<Results<Ok<ArtistDetailDto>, NotFound>> GetArtistAsync(
        string key,
        ICurrentUser currentUser,
        JukeBoxDbContext database,
        CancellationToken cancellationToken)
    {
        if (GroupKey.ReadArtist(key) is not { } decoded)
        {
            return TypedResults.NotFound();
        }

        var artist = Blank(decoded);

        var tracks = await Available(database, currentUser.RequireId())
            .Where(t => t.Artist == artist)
            .OrderBy(t => t.Album)
            .ThenBy(t => t.DiscNumber ?? 1)
            .ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.Title)
            .Select(t => new BrowseTrackDto(
                t.Id,
                t.Title,
                t.Artist,
                t.Album,
                t.TrackNumber,
                t.DiscNumber,
                t.DurationSeconds,
                t.ArtSha256,
                t.PlayCount))
            .ToListAsync(cancellationToken);

        if (tracks.Count == 0)
        {
            return TypedResults.NotFound();
        }

        // Grouped after the fact rather than in a second query. The tracks are already loaded and
        // an artist's discography is small; a second round trip would buy nothing.
        var albums = tracks
            .GroupBy(t => t.Album)
            .Select(g => new ArtistAlbumDto(
                GroupKey.ForAlbum(artist, g.Key),
                g.Key,
                g.Count(),
                g.Sum(t => t.DurationSeconds ?? 0),
                g.Select(t => t.ArtSha256).FirstOrDefault(sha => sha is not null)))
            .OrderBy(a => a.Album, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return TypedResults.Ok(new ArtistDetailDto(
            key,
            artist,
            tracks.Count,
            tracks.Sum(t => t.DurationSeconds ?? 0),
            tracks.Select(t => t.ArtSha256).FirstOrDefault(sha => sha is not null),
            albums,
            tracks));
    }

    /// <summary>This user's tracks that can actually be played.</summary>
    private static IQueryable<Track> Available(JukeBoxDbContext database, Guid userId) =>
        database.Tracks.AsNoTracking().Where(t => t.UserId == userId && t.DeletedAtUtc == null);

    /// <summary>
    /// Treats an empty key half as an absent tag.
    /// </summary>
    /// <remarks>
    /// Grouping puts untagged tracks under a null artist or album; a URL key cannot carry null,
    /// so it round-trips as an empty string. Converting back here is what makes the "no album"
    /// group reachable rather than a dead link.
    /// </remarks>
    private static string? Blank(string value) => value.Length == 0 ? null : value;
}

/// <param name="Truncated">
/// True when the library has more groups than one response returns. Reported so the view can say
/// so, rather than silently showing a prefix.
/// </param>
public sealed record GroupPageDto<T>(int Count, bool Truncated, IReadOnlyList<T> Items);

public sealed record AlbumDto(
    string Key,
    string? Album,
    string? AlbumArtist,
    int TrackCount,
    int DurationSeconds,
    string? ArtSha256,
    int? Year,
    int PlayCount);

public sealed record AlbumDetailDto(
    string Key,
    string? Album,
    string? AlbumArtist,
    int DurationSeconds,
    string? ArtSha256,
    IReadOnlyList<BrowseTrackDto> Tracks);

public sealed record ArtistDto(
    string Key,
    string? Artist,
    int AlbumCount,
    int TrackCount,
    int DurationSeconds,
    string? ArtSha256,
    int PlayCount);

public sealed record ArtistAlbumDto(
    string Key,
    string? Album,
    int TrackCount,
    int DurationSeconds,
    string? ArtSha256);

public sealed record ArtistDetailDto(
    string Key,
    string? Artist,
    int TrackCount,
    int DurationSeconds,
    string? ArtSha256,
    IReadOnlyList<ArtistAlbumDto> Albums,
    IReadOnlyList<BrowseTrackDto> Tracks);

public sealed record BrowseTrackDto(
    Guid Id,
    string Title,
    string? Artist,
    string? Album,
    int? TrackNumber,
    int? DiscNumber,
    int? DurationSeconds,
    string? ArtSha256,
    int PlayCount);
