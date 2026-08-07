using TheJukeBox.Infrastructure.Metadata.Online;

namespace TheJukeBox.Tests;

/// <summary>
/// Deciding whether a search result really is the file on disk.
/// </summary>
/// <remarks>
/// The judgement in this feature, and the only part of it worth testing hard. A missing cover reads
/// as "not found yet"; a wrong cover reads as a broken application, and nobody goes looking for the
/// setting that caused it. So most of these assert a rejection.
/// </remarks>
public class RecordingMatcherTests
{
    private const int Score = 90;
    private const int Tolerance = 8;

    private static RecordingCandidate Candidate(
        string title = "Paranoid Android",
        string? artist = "Radiohead",
        int score = 100,
        int? length = 383,
        string? releaseId = "release-1") =>
        new(
            "recording-1",
            title,
            artist,
            releaseId is null ? [] : [new CandidateRelease(releaseId, "OK Computer", 1997)],
            score,
            length);

    private static LookupQuery Query(
        string title = "Paranoid Android",
        string? artist = "Radiohead",
        string? album = "OK Computer",
        int? duration = 383) =>
        new(title, artist, album, duration);

    private static MatchResult Choose(LookupQuery query, params RecordingCandidate[] candidates) =>
        RecordingMatcher.Choose(query, candidates, Score, Tolerance);

    [Fact]
    public void An_exact_match_is_accepted()
    {
        var result = Choose(Query(), Candidate());

        Assert.Equal(MatchRejection.None, result.Rejection);
        Assert.Equal("recording-1", result.Match?.RecordingId);
    }

    [Fact]
    public void Nothing_to_choose_from_is_reported_as_such() =>
        Assert.Equal(MatchRejection.NoCandidates, Choose(Query()).Rejection);

    [Fact]
    public void A_low_relevance_score_is_rejected()
    {
        var result = Choose(Query(), Candidate(score: 72));

        Assert.Null(result.Match);
        Assert.Equal(MatchRejection.ScoreTooLow, result.Rejection);
    }

    /// <summary>
    /// The signal that actually distinguishes recordings. A cover, a live take and a remaster all
    /// share a title and an artist and score highly; they differ here.
    /// </summary>
    [Fact]
    public void A_recording_of_a_different_length_is_rejected()
    {
        var result = Choose(Query(duration: 383), Candidate(length: 254));

        Assert.Null(result.Match);
        Assert.Equal(MatchRejection.DurationDisagrees, result.Rejection);
    }

    [Theory]
    [InlineData(383, 383)]
    [InlineData(383, 380)]
    [InlineData(383, 391)]
    public void A_length_within_tolerance_is_accepted(int fileSeconds, int candidateSeconds) =>
        Assert.NotNull(Choose(Query(duration: fileSeconds), Candidate(length: candidateSeconds)).Match);

    [Theory]
    [InlineData(383, 374)]
    [InlineData(383, 392)]
    public void A_length_outside_tolerance_is_not(int fileSeconds, int candidateSeconds) =>
        Assert.Null(Choose(Query(duration: fileSeconds), Candidate(length: candidateSeconds)).Match);

    /// <summary>
    /// A duration check needs two durations. Refusing every track whose length is unknown would
    /// exclude exactly the untagged files this feature exists to help.
    /// </summary>
    [Theory]
    [InlineData(null, 383)]
    [InlineData(383, null)]
    [InlineData(null, null)]
    public void An_unknown_length_on_either_side_does_not_reject(int? fileSeconds, int? candidateSeconds) =>
        Assert.NotNull(Choose(Query(duration: fileSeconds), Candidate(length: candidateSeconds)).Match);

    [Fact]
    public void A_different_title_is_rejected()
    {
        var result = Choose(Query(title: "Karma Police"), Candidate(title: "Paranoid Android"));

        Assert.Null(result.Match);
        Assert.Equal(MatchRejection.DifferentTitle, result.Rejection);
    }

    [Fact]
    public void A_different_artist_is_rejected()
    {
        var result = Choose(Query(artist: "Muse"), Candidate(artist: "Radiohead"));

        Assert.Null(result.Match);
        Assert.Equal(MatchRejection.DifferentArtist, result.Rejection);
    }

    /// <summary>
    /// A filename-derived query usually has no artist at all. Requiring one would reject the whole
    /// category this is for.
    /// </summary>
    [Fact]
    public void An_unknown_artist_is_not_held_against_a_candidate() =>
        Assert.NotNull(Choose(Query(artist: null), Candidate()).Match);

    /// <summary>
    /// The shapes a downloaded file's name actually takes. These are the same recording, and
    /// rejecting them would leave most of a downloaded library unmatched.
    /// </summary>
    [Theory]
    [InlineData("Paranoid Android (Official Video)")]
    [InlineData("Paranoid Android [HD]")]
    [InlineData("Paranoid Android (Lyrics)")]
    [InlineData("paranoid android")]
    [InlineData("PARANOID ANDROID")]
    public void Presentation_noise_around_a_title_still_matches(string title) =>
        Assert.NotNull(Choose(Query(title: title), Candidate()).Match);

    /// <summary>
    /// Whitespace is not part of a name's identity, and the two sides disagree about it constantly.
    /// A real library had a track filed as "selfcare" against a database entry of "Self Care".
    /// </summary>
    [Theory]
    [InlineData("selfcare", "Self Care")]
    [InlineData("NothingElseMatters", "Nothing Else Matters")]
    [InlineData("Self-Care", "Self Care")]
    public void Spacing_does_not_prevent_a_match(string fileTitle, string candidateTitle) =>
        Assert.NotNull(Choose(
            Query(title: fileTitle, artist: null, duration: null),
            Candidate(title: candidateTitle, length: null)).Match);

    [Theory]
    [InlineData("Sigur Rós", "Sigur Ros")]
    [InlineData("Beyoncé", "Beyonce")]
    [InlineData("Motörhead", "Motorhead")]
    public void Accents_do_not_prevent_a_match(string fileArtist, string candidateArtist) =>
        Assert.NotNull(Choose(Query(artist: fileArtist), Candidate(artist: candidateArtist)).Match);

    /// <summary>
    /// Containment is deliberately loose, so it needs a floor. Without one a track called "Go"
    /// would match anything containing those two letters.
    /// </summary>
    [Fact]
    public void A_very_short_title_must_match_exactly()
    {
        Assert.Null(Choose(Query(title: "Go"), Candidate(title: "Going Under")).Match);
        Assert.NotNull(Choose(Query(title: "Go"), Candidate(title: "Go")).Match);
    }

    [Fact]
    public void The_highest_scoring_acceptable_candidate_wins()
    {
        var weak = Candidate(score: 91) with { RecordingId = "weak" };
        var strong = Candidate(score: 99) with { RecordingId = "strong" };

        Assert.Equal("strong", Choose(Query(), weak, strong).Match?.RecordingId);
    }

    /// <summary>
    /// A high-scoring wrong answer must not shadow a lower-scoring right one. This is the case that
    /// makes the difference between "usually correct" and "correct".
    /// </summary>
    [Fact]
    public void A_lower_scoring_candidate_is_used_when_the_best_one_fails_a_check()
    {
        var wrongLength = Candidate(score: 100, length: 120) with { RecordingId = "wrong" };
        var right = Candidate(score: 92, length: 383) with { RecordingId = "right" };

        Assert.Equal("right", Choose(Query(duration: 383), wrongLength, right).Match?.RecordingId);
    }

    /// <summary>
    /// The reason reported is the closest candidate's. "The nearest thing we found was a different
    /// length" is useful in a log; "the fifth result had a different artist" is noise.
    /// </summary>
    [Fact]
    public void The_reported_reason_comes_from_the_best_candidate()
    {
        var best = Candidate(score: 100, length: 120);
        var worse = Candidate(score: 95, artist: "Someone Else", length: 383);

        Assert.Equal(MatchRejection.DurationDisagrees, Choose(Query(), best, worse).Rejection);
    }

    /// <summary>
    /// Cover art is the point of most lookups, and it is keyed by release. Among equals, one that
    /// can actually yield artwork is worth more.
    /// </summary>
    [Fact]
    public void A_candidate_with_a_release_is_preferred_over_one_without()
    {
        var bare = Candidate(releaseId: null) with { RecordingId = "bare" };
        var withRelease = Candidate() with { RecordingId = "with-release" };

        Assert.Equal("with-release", Choose(Query(), bare, withRelease).Match?.RecordingId);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "anything")]
    [InlineData("anything", null)]
    public void An_empty_name_never_counts_as_similar(string? left, string? right) =>
        Assert.False(RecordingMatcher.Similar(left, right));

    /// <summary>A title that is nothing but a bracketed phrase keeps it, or nothing is left to match.</summary>
    [Fact]
    public void A_wholly_bracketed_title_is_not_stripped_to_nothing() =>
        Assert.Equal("untitled", RecordingMatcher.Normalise("(Untitled)"));

    /// <summary>
    /// The ladder that rescues a track whose "album" is really a folder name.
    /// </summary>
    /// <remarks>
    /// This was not hypothetical: ten untagged files carried an album tag of "Darien Çepani 2-6",
    /// the folder they were ripped into, and every constrained query returned nothing at all --
    /// including for Stairway to Heaven, which MusicBrainz has sixteen hundred entries for.
    /// </remarks>
    [Fact]
    public void The_query_ladder_drops_constraints_one_at_a_time()
    {
        var ladder = MusicBrainzClient
            .QueryLadder(new LookupQuery("Stairway to Heaven", "Led Zeppelin", "Some Folder", 482))
            .ToList();

        Assert.Equal(3, ladder.Count);
        Assert.Contains("release:", ladder[0], StringComparison.Ordinal);
        Assert.Contains("artist:", ladder[1], StringComparison.Ordinal);
        Assert.DoesNotContain("release:", ladder[1], StringComparison.Ordinal);
        Assert.DoesNotContain("artist:", ladder[2], StringComparison.Ordinal);
        Assert.All(ladder, step => Assert.Contains("recording:", step, StringComparison.Ordinal));
    }

    /// <summary>
    /// One rung when there is nothing to drop. At one request per second, duplicate rungs are the
    /// whole runtime of a pass.
    /// </summary>
    [Fact]
    public void The_ladder_has_no_duplicate_rungs()
    {
        var ladder = MusicBrainzClient.QueryLadder(new LookupQuery("Selfcare", null, null, 200)).ToList();

        Assert.Single(ladder);
    }

    [Fact]
    public void A_track_with_no_title_is_not_searched_for() =>
        Assert.Empty(MusicBrainzClient.QueryLadder(new LookupQuery("  ", "Someone", "Something", 100)));

    /// <summary>
    /// Lucene treats a great deal of punctuation as syntax, and track titles are full of it. An
    /// unescaped quote is a parse error rather than a search.
    /// </summary>
    [Fact]
    public void Quotes_in_a_title_are_escaped()
    {
        var query = MusicBrainzClient.QueryLadder(new LookupQuery("He said \"no\"", null, null, null)).Single();

        Assert.Equal("recording:\"He said \\\"no\\\"\"", query);
    }

    [Theory]
    [InlineData("Song — Remastered", "song remastered")]
    [InlineData("  Spaced   Out  ", "spaced out")]
    [InlineData("AC/DC", "ac dc")]
    [InlineData("Song (Live) [2011]", "song")]
    public void Normalisation_reduces_a_name_to_its_words(string input, string expected) =>
        Assert.Equal(expected, RecordingMatcher.Normalise(input));
}
