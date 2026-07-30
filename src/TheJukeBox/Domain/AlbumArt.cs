namespace TheJukeBox.Domain;

/// <summary>
/// One piece of cover art, keyed by the hash of its own bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content-addressed, and that is the whole design.</b> The twelve tracks ripped from
/// one album carry byte-identical embedded pictures, so hashing the original bytes before
/// anything re-encodes them collapses an album to a single row. No album-grouping logic
/// exists anywhere in this application, and none is needed: the deduplication falls out of
/// the key. It also dedupes across users who happen to hold the same album, and across
/// albums that share a label's artwork.
/// </para>
/// <para>
/// <b>Not scoped to a user, on purpose.</b> A hash is not a user's secret and the row holds
/// nothing identifying, so scoping it per user would multiply storage by the number of
/// listeners to buy nothing. The serving route is still behind authentication, and the key
/// is 128 bits of a SHA-256, so it cannot be enumerated or guessed by someone who does not
/// already have the image.
/// </para>
/// <para>
/// <b>Bytes in the database.</b> Simplest thing that works at this scale: a few thousand
/// covers is tens of megabytes, SQLite handles that without complaint, and it keeps backup
/// and restore to one file. Moving to a filesystem cache later is one class change and no
/// API change, precisely because the route is content-addressed: the URL names the bytes,
/// not their location.
/// </para>
/// </remarks>
public sealed class AlbumArt
{
    /// <summary>
    /// Primary key: lowercase hex SHA-256 of <see cref="Bytes"/>, truncated.
    /// </summary>
    /// <remarks>
    /// 32 hex characters is 128 bits, which for a collection numbered in thousands leaves
    /// the collision probability at roughly 10^-30. The full 64 characters would double the
    /// size of every index entry and every URL for no practical gain.
    /// </remarks>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The original embedded bytes, unmodified.</summary>
    public byte[] Bytes { get; set; } = [];

    /// <summary>
    /// The MIME type the tag declared, or a sniffed one. Never trusted from the tag alone.
    /// </summary>
    /// <remarks>
    /// A tag can declare anything, and this value ends up in a <c>Content-Type</c> header.
    /// Serving an attacker-influenced type is how an image endpoint becomes an HTML
    /// injection, so the stored value is validated against a small allow-list on the way in.
    /// </remarks>
    public string MimeType { get; set; } = "image/jpeg";

    public int SizeBytes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
