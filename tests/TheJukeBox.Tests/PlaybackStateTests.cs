using TheJukeBox.Domain;
using TheJukeBox.Features.Playlists;

namespace TheJukeBox.Tests;

/// <summary>
/// The saved queue's storage format.
/// </summary>
/// <remarks>
/// The queue lives in one fixed-width column rather than a child table, so the codec is the
/// only thing standing between a corrupt value and the resume path. Its contract is that
/// nothing it reads can throw.
/// </remarks>
public class TrackIdCodecTests
{
    [Fact]
    public void Round_trips_a_queue()
    {
        List<Guid> ids = [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()];

        Assert.Equal(ids, TrackIdCodec.Decode(TrackIdCodec.Encode(ids)));
    }

    [Fact]
    public void Encodes_thirty_two_characters_per_id_with_no_separator()
    {
        List<Guid> ids = [Guid.CreateVersion7(), Guid.CreateVersion7()];

        var encoded = TrackIdCodec.Encode(ids);

        Assert.Equal(64, encoded.Length);
        Assert.DoesNotContain('-', encoded);
    }

    [Fact]
    public void An_empty_queue_round_trips_as_an_empty_string()
    {
        Assert.Equal(string.Empty, TrackIdCodec.Encode([]));
        Assert.Empty(TrackIdCodec.Decode(string.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Rubbish_decodes_to_nothing_rather_than_throwing(string? encoded) =>
        Assert.Empty(TrackIdCodec.Decode(encoded));

    /// <summary>
    /// The realistic corruption: the column was truncated. The whole ids before the cut must
    /// still come back, because losing the queue entirely is a worse outcome than losing its
    /// tail.
    /// </summary>
    [Fact]
    public void A_truncated_value_keeps_the_ids_that_survived()
    {
        List<Guid> ids = [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()];

        var encoded = TrackIdCodec.Encode(ids);

        // Two whole ids and half of the third.
        var truncated = encoded[..(32 * 2 + 16)];

        Assert.Equal(ids.Take(2), TrackIdCodec.Decode(truncated));
    }

    /// <summary>
    /// One bad id in the middle must not discard the ones after it. A parser that gave up on
    /// the first failure would lose the whole queue to a single mangled entry.
    /// </summary>
    [Fact]
    public void A_single_unparseable_id_does_not_discard_its_neighbours()
    {
        var first = Guid.CreateVersion7();
        var last = Guid.CreateVersion7();

        var encoded = first.ToString("N") + new string('z', 32) + last.ToString("N");

        Assert.Equal([first, last], TrackIdCodec.Decode(encoded));
    }

    /// <summary>
    /// The cap is what keeps a row that is rewritten every few seconds from reaching megabytes
    /// when someone shuffles a 40,000-track library.
    /// </summary>
    [Fact]
    public void Encoding_stops_at_the_queue_cap()
    {
        var ids = Enumerable.Range(0, PlaybackState.MaxQueueLength + 500)
            .Select(_ => Guid.CreateVersion7())
            .ToList();

        var encoded = TrackIdCodec.Encode(ids);

        Assert.Equal(PlaybackState.MaxQueueLength * 32, encoded.Length);
        Assert.Equal(ids.Take(PlaybackState.MaxQueueLength), TrackIdCodec.Decode(encoded));
    }

    [Fact]
    public void Decoding_stops_at_the_queue_cap()
    {
        // A hand-written value longer than the cap, as a hostile or legacy row would be.
        var oversized = string.Concat(
            Enumerable.Range(0, PlaybackState.MaxQueueLength + 100).Select(_ => Guid.CreateVersion7().ToString("N")));

        Assert.Equal(PlaybackState.MaxQueueLength, TrackIdCodec.Decode(oversized).Count);
    }

    [Fact]
    public void The_column_is_wide_enough_for_a_full_queue() =>
        Assert.Equal(PlaybackState.MaxQueueLength * 32, TrackIdCodec.Encode(
            [.. Enumerable.Range(0, PlaybackState.MaxQueueLength).Select(_ => Guid.CreateVersion7())]).Length);
}

/// <summary>
/// The entity's defaults, which are what a user with no saved state gets.
/// </summary>
public class PlaybackStateDefaultTests
{
    [Fact]
    public void A_fresh_state_has_nothing_cued()
    {
        var state = new PlaybackState();

        Assert.Equal(-1, state.Cursor);
        Assert.Equal(RepeatMode.Off, state.Repeat);
        Assert.False(state.Shuffle);
        Assert.Equal(string.Empty, state.QueuedTrackIds);
        Assert.Equal(0, state.PositionSeconds);
    }

    /// <summary>
    /// The empty DTO is what the API returns before anything has ever been saved, and the
    /// client treats cursor -1 as "nothing cued". A 0 here would make it try to play a track
    /// that is not there.
    /// </summary>
    [Fact]
    public void The_empty_dto_matches_the_entity_defaults()
    {
        Assert.Empty(PlaybackStateDto.Empty.TrackIds);
        Assert.Equal(-1, PlaybackStateDto.Empty.Cursor);
        Assert.Equal(nameof(RepeatMode.Off), PlaybackStateDto.Empty.Repeat);
        Assert.Null(PlaybackStateDto.Empty.UpdatedAtUtc);
    }

    /// <summary>
    /// A skip must not be logged. Otherwise "recently played" becomes a list of things the
    /// user rejected, and the play counts that drive "most played" become meaningless.
    /// </summary>
    [Fact]
    public void The_listen_threshold_excludes_flicking_through_a_queue() =>
        Assert.InRange(PlayHistory.MinimumSeconds, 5, 60);
}
