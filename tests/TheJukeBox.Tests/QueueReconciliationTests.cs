using TheJukeBox.Features.Playlists;

namespace TheJukeBox.Tests;

/// <summary>
/// Restoring a saved queue against a library that has moved on.
/// </summary>
/// <remarks>
/// Pure, so the awkward cases are cheap to enumerate. The one that matters is an entry dropped
/// from before the cursor: the arithmetic version of this function resumes one track late,
/// silently, and only for users who have trashed something in Drive.
/// </remarks>
public class QueueReconciliationTests
{
    private static readonly Guid[] Ids =
        [.. Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7())];

    [Fact]
    public void An_intact_queue_is_returned_unchanged()
    {
        var (queue, cursor, position) = Reconcile([0, 1, 2, 3], playable: [0, 1, 2, 3], cursor: 2, seconds: 42);

        Assert.Equal(Pick(0, 1, 2, 3), queue);
        Assert.Equal(2, cursor);
        Assert.Equal(42, position);
    }

    /// <summary>
    /// The case the arithmetic version gets wrong. Index 0 disappears, so the track that was at
    /// index 2 is now at index 1 -- and it must still be the one that resumes.
    /// </summary>
    [Fact]
    public void A_track_removed_before_the_cursor_shifts_the_cursor_with_it()
    {
        var (queue, cursor, position) = Reconcile([0, 1, 2, 3], playable: [1, 2, 3], cursor: 2, seconds: 42);

        Assert.Equal(Pick(1, 2, 3), queue);
        Assert.Equal(1, cursor);
        Assert.Equal(Ids[2], queue[cursor]);
        Assert.Equal(42, position);
    }

    [Fact]
    public void Several_removals_before_the_cursor_are_all_accounted_for()
    {
        var (queue, cursor, _) = Reconcile([0, 1, 2, 3, 4, 5], playable: [2, 4, 5], cursor: 4, seconds: 10);

        Assert.Equal(Pick(2, 4, 5), queue);
        Assert.Equal(Ids[4], queue[cursor]);
    }

    [Fact]
    public void A_track_removed_after_the_cursor_leaves_the_cursor_alone()
    {
        var (queue, cursor, _) = Reconcile([0, 1, 2, 3], playable: [0, 1, 2], cursor: 1, seconds: 5);

        Assert.Equal(Pick(0, 1, 2), queue);
        Assert.Equal(1, cursor);
    }

    /// <summary>
    /// The cursor's own track is gone. There is nothing to resume, and resuming 40 minutes into
    /// whatever took its place would be worse than starting over.
    /// </summary>
    [Fact]
    public void Losing_the_cursors_own_track_clears_the_cursor_and_the_offset()
    {
        var (queue, cursor, position) = Reconcile([0, 1, 2], playable: [0, 2], cursor: 1, seconds: 2400);

        Assert.Equal(Pick(0, 2), queue);
        Assert.Equal(-1, cursor);
        Assert.Equal(0, position);
    }

    [Fact]
    public void An_empty_playable_set_empties_the_queue()
    {
        var (queue, cursor, position) = Reconcile([0, 1, 2], playable: [], cursor: 0, seconds: 30);

        Assert.Empty(queue);
        Assert.Equal(-1, cursor);
        Assert.Equal(0, position);
    }

    [Fact]
    public void An_empty_saved_queue_stays_empty()
    {
        var (queue, cursor, _) = Reconcile([], playable: [0, 1], cursor: -1, seconds: 0);

        Assert.Empty(queue);
        Assert.Equal(-1, cursor);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void A_cursor_outside_the_queue_resolves_to_nothing_cued(int cursor)
    {
        var (queue, resolved, position) = Reconcile([0, 1, 2], playable: [0, 1, 2], cursor: cursor, seconds: 15);

        Assert.Equal(3, queue.Count);
        Assert.Equal(-1, resolved);
        Assert.Equal(0, position);
    }

    /// <summary>
    /// The same track twice is legitimate, and the cursor must land on the copy that was
    /// playing. A lookup by id would find the first copy and resume the wrong one.
    /// </summary>
    [Fact]
    public void A_duplicated_track_resumes_on_the_copy_that_was_playing()
    {
        var (queue, cursor, _) = Reconcile([0, 1, 0], playable: [0, 1], cursor: 2, seconds: 7);

        Assert.Equal(Pick(0, 1, 0), queue);
        Assert.Equal(2, cursor);
    }

    [Fact]
    public void Duplicates_survive_reconciliation()
    {
        var (queue, _, _) = Reconcile([0, 0, 0], playable: [0], cursor: 0, seconds: 0);

        Assert.Equal(3, queue.Count);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5)]
    public void A_nonsensical_offset_becomes_zero(double seconds)
    {
        var (_, _, position) = Reconcile([0], playable: [0], cursor: 0, seconds: seconds);

        Assert.Equal(0, position);
    }

    private static (List<Guid> Queue, int Cursor, double Position) Reconcile(
        int[] saved,
        int[] playable,
        int cursor,
        double seconds) =>
        QueueReconciliation.Apply(
            [.. saved.Select(i => Ids[i])],
            playable.Select(i => Ids[i]).ToHashSet(),
            cursor,
            seconds);

    private static List<Guid> Pick(params int[] indices) => [.. indices.Select(i => Ids[i])];
}
