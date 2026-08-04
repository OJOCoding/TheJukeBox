namespace TheJukeBox.Features.Playlists;

/// <summary>
/// Brings a saved queue back into line with the library it refers to.
/// </summary>
/// <remarks>
/// A queue can be saved on Monday and restored on Friday, by which time files have been
/// trashed in Drive and swept out of the library. Handing those ids back would give the user a
/// queue whose entries fail when played, with no explanation.
/// </remarks>
internal static class QueueReconciliation
{
    /// <summary>
    /// Drops unplayable entries and moves the cursor to wherever its track ended up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cursor is resolved to a track id before filtering and looked up again afterwards,
    /// rather than adjusted by counting removals. Arithmetic is the tempting version and it is
    /// wrong in a way that is hard to see: an entry dropped from *before* the cursor shifts
    /// every later index, so a queue that lost its second track would resume one track late,
    /// forever, and only for users who had trashed something.
    /// </para>
    /// <para>
    /// When the cursor's own track is gone there is nothing to resume, so it returns -1 and a
    /// zero offset -- an offset into a track that is no longer there would be meaningless, and
    /// resuming 40 minutes into a different track is worse than starting it.
    /// </para>
    /// </remarks>
    public static (List<Guid> TrackIds, int Cursor, double PositionSeconds) Apply(
        IReadOnlyList<Guid> saved,
        IReadOnlySet<Guid> playable,
        int cursor,
        double positionSeconds)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(playable);

        var cursorId = cursor >= 0 && cursor < saved.Count ? saved[cursor] : (Guid?)null;

        var queue = new List<Guid>(saved.Count);

        // Duplicates are preserved: the same track twice in a queue is legitimate, and
        // deduplicating here would silently shorten it.
        foreach (var id in saved)
        {
            if (playable.Contains(id))
            {
                queue.Add(id);
            }
        }

        if (cursorId is null || !playable.Contains(cursorId.Value))
        {
            return (queue, -1, 0);
        }

        // How many surviving entries precede the cursor. IndexOf would find the *first* copy of
        // a duplicated track, which for a queue holding the same track twice would resume at
        // the wrong one.
        var index = 0;

        for (var position = 0; position < cursor; position++)
        {
            if (playable.Contains(saved[position]))
            {
                index++;
            }
        }

        return (queue, index, double.IsFinite(positionSeconds) ? Math.Max(positionSeconds, 0) : 0);
    }
}
