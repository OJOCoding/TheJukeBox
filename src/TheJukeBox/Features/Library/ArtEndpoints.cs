using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using TheJukeBox.Errors;
using TheJukeBox.Infrastructure.Persistence;

namespace TheJukeBox.Features.Library;

/// <summary>
/// Serves cover art by content hash.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content-addressed, so caching is trivially correct.</b> The URL names the bytes. Those
/// bytes can never change for that URL, so the response carries a one-year
/// <c>max-age</c> with <c>immutable</c> and a strong <c>ETag</c>, and neither can ever be
/// wrong. A per-track art URL would have needed revalidation on every request, because a
/// track's art can change.
/// </para>
/// <para>
/// <b>Behind authentication, deliberately.</b> There is a real performance argument for
/// serving art anonymously: a browser sends no credentials on an anonymous image request,
/// so it caches more aggressively and skips a cookie round trip. It is refused anyway. This
/// is a private library, an unauthenticated endpoint returning bytes keyed by a value that
/// appears in an authenticated JSON payload is an exfiltration path, and a cookie check
/// costs nothing at ten users.
/// </para>
/// <para>
/// <b>Resizing was descoped, and the route still carries a size.</b> Nothing here resizes:
/// both sizes return the original embedded bytes. Re-encoding needs an imaging library, and
/// the obvious candidate (SixLabors.ImageSharp) moved to the Six Labors Split License at
/// version 3, which is a licence question this MIT repository should not answer casually for
/// a cosmetic win. The size is in the route from the start so that adding a real resize
/// later is a change to this one handler and a cache table, with no client change and no URL
/// change. Covers are typically 100-600 KB, which is acceptable for a 48-pixel transport
/// thumbnail on a self-hosted app and would not be for a public one.
/// </para>
/// </remarks>
public static partial class ArtEndpoints
{
    /// <summary>
    /// The sizes the route accepts. Closed set, so the path cannot be used to ask the server
    /// to perform arbitrary work later.
    /// </summary>
    private static readonly int[] AllowedSizes = [96, 640];

    /// <summary>
    /// Lowercase hex only, and length-bounded. This value is a primary key lookup, so
    /// rejecting anything else keeps malformed input from reaching the database at all.
    /// </summary>
    [GeneratedRegex("^[0-9a-f]{32,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtHash { get; }

    public static void MapArtEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/api/art/{sha256}/{size:int}", GetArtAsync)
            .RequireAuthorization()
            .WithTags("Library");
    }

    private static async Task<IResult> GetArtAsync(
        string sha256,
        int size,
        HttpContext httpContext,
        JukeBoxDbContext database,
        CancellationToken cancellationToken)
    {
        if (!ArtHash.IsMatch(sha256) || !AllowedSizes.Contains(size))
        {
            throw new ResourceNotFoundException("No such image.");
        }

        var art = await database.AlbumArt
            .AsNoTracking()
            .Where(a => a.Sha256 == sha256)
            .Select(a => new { a.Bytes, a.MimeType })
            .FirstOrDefaultAsync(cancellationToken);

        if (art is null)
        {
            throw new ResourceNotFoundException("No such image.");
        }

        var response = httpContext.Response;

        // Private, not public: the response is authorized, so a shared proxy must not keep
        // a copy. Immutable is safe only because the URL is content-addressed.
        response.Headers.CacheControl = "private, max-age=31536000, immutable";

        // The bytes are third-party content from a tag. The type was decided by sniffing the
        // magic bytes rather than trusting the tag, and nosniff stops the browser
        // second-guessing that decision back towards something scriptable.
        response.Headers.XContentTypeOptions = "nosniff";

        // Passing the ETag to Results.Bytes rather than setting the header by hand is what
        // gets If-None-Match handling, and therefore the 304, without writing it.
        return Results.Bytes(
            art.Bytes,
            art.MimeType,
            entityTag: new EntityTagHeaderValue($"\"{sha256}\""));
    }
}
