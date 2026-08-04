namespace TheJukeBox.Features.Playlists;

/// <summary>
/// Packs and unpacks the saved queue's track ids.
/// </summary>
/// <remarks>
/// <para>
/// The queue is stored as one fixed-width string of 32-character hex ids: see
/// <see cref="Domain.PlaybackState.QueuedTrackIds"/> for why it is one column rather than a
/// child table.
/// </para>
/// <para>
/// Fixed-width and separator-free is the point. There is no delimiter a value could contain,
/// nothing to escape, and no parser state -- decoding is a bounds check and a slice, so a
/// corrupt or truncated column degrades to "some ids were dropped" rather than to an exception
/// on every page load.
/// </para>
/// </remarks>
internal static class TrackIdCodec
{
    /// <summary>A GUID in "N" format: 32 hex characters, no dashes or braces.</summary>
    private const int IdLength = 32;

    public static string Encode(IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var count = Math.Min(ids.Count, Domain.PlaybackState.MaxQueueLength);
        var builder = new System.Text.StringBuilder(count * IdLength);

        for (var index = 0; index < count; index++)
        {
            builder.Append(ids[index].ToString("N"));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads the ids back, discarding anything unparseable.
    /// </summary>
    /// <remarks>
    /// A trailing partial id, or hex that is not a GUID, is skipped rather than thrown. This
    /// value is only ever a convenience -- where the user was -- and failing a page load over
    /// it would trade a small loss for a total one.
    /// </remarks>
    public static List<Guid> Decode(string? encoded)
    {
        var ids = new List<Guid>();

        if (string.IsNullOrEmpty(encoded))
        {
            return ids;
        }

        var whole = encoded.Length / IdLength;

        for (var index = 0; index < whole && ids.Count < Domain.PlaybackState.MaxQueueLength; index++)
        {
            var slice = encoded.AsSpan(index * IdLength, IdLength);

            if (Guid.TryParseExact(slice, "N", out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
