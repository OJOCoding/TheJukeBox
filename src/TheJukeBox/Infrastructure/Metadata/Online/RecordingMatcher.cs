using System.Globalization;
using System.Text;

namespace TheJukeBox.Infrastructure.Metadata.Online;

/// <summary>What is known about a local file before looking it up.</summary>
public sealed record LookupQuery(string Title, string? Artist, string? Album, int? DurationSeconds);

/// <summary>One candidate returned by MusicBrainz.</summary>
/// <param name="Score">Relevance out of 100, as the service scored it.</param>
/// <param name="LengthSeconds">The recording's length, when the database knows it.</param>
public sealed record RecordingCandidate(
    string RecordingId,
    string Title,
    string? Artist,
    IReadOnlyList<CandidateRelease> Releases,
    int Score,
    int? LengthSeconds);

/// <param name="ReleaseId">A MusicBrainz release MBID, which is what Cover Art Archive is keyed by.</param>
public sealed record CandidateRelease(string ReleaseId, string? Title, int? Year);

/// <summary>Why a lookup produced nothing, for the log and for the tests.</summary>
public enum MatchRejection
{
    None = 0,
    NoCandidates = 1,
    ScoreTooLow = 2,
    DurationDisagrees = 3,
    DifferentTitle = 4,
    DifferentArtist = 5,
}

public sealed record MatchResult(RecordingCandidate? Match, MatchRejection Rejection);

/// <summary>
/// Decides whether a search result is the same recording as the file on disk.
/// </summary>
/// <remarks>
/// <para>
/// Kept pure and separate from the HTTP client, because this is where the judgement lives and
/// judgement is what needs testing. The client's job is to ask a question; this decides whether
/// the answer is good enough to write into someone's library.
/// </para>
/// <para>
/// <b>Biased hard towards rejecting.</b> A missing cover reads as "not found yet". A wrong cover
/// reads as a broken application, and nobody goes hunting for the setting that caused it. Every
/// rule here exists to turn a plausible-but-wrong candidate into no candidate at all.
/// </para>
/// </remarks>
public static class RecordingMatcher
{
    /// <summary>
    /// Picks the best acceptable candidate, or explains why none was.
    /// </summary>
    public static MatchResult Choose(
        LookupQuery query,
        IReadOnlyList<RecordingCandidate> candidates,
        int minimumScore,
        int durationToleranceSeconds)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return new MatchResult(null, MatchRejection.NoCandidates);
        }

        // Ordered by the service's own relevance, then by having a release at all: a recording with
        // no release cannot yield cover art, which is the main thing a lookup is for.
        var ordered = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Releases.Count > 0)
            .ToList();

        var rejection = MatchRejection.NoCandidates;

        foreach (var candidate in ordered)
        {
            var verdict = Evaluate(query, candidate, minimumScore, durationToleranceSeconds);

            if (verdict == MatchRejection.None)
            {
                return new MatchResult(candidate, MatchRejection.None);
            }

            // The reason reported is the best candidate's, not the last one's: "the closest thing
            // we found was a different length" is useful, "the fifth result had a different artist"
            // is noise.
            if (rejection == MatchRejection.NoCandidates)
            {
                rejection = verdict;
            }
        }

        return new MatchResult(null, rejection);
    }

    private static MatchRejection Evaluate(
        LookupQuery query,
        RecordingCandidate candidate,
        int minimumScore,
        int durationToleranceSeconds)
    {
        if (candidate.Score < minimumScore)
        {
            return MatchRejection.ScoreTooLow;
        }

        // The strongest signal, and the only one that is a measurement rather than a string.
        // Checked before the text, because a cover, a live take and a remaster all share a title
        // and an artist and differ here.
        if (query.DurationSeconds is > 0 && candidate.LengthSeconds is > 0)
        {
            var difference = Math.Abs(query.DurationSeconds.Value - candidate.LengthSeconds.Value);

            if (difference > durationToleranceSeconds)
            {
                return MatchRejection.DurationDisagrees;
            }
        }

        if (!Similar(query.Title, candidate.Title))
        {
            return MatchRejection.DifferentTitle;
        }

        // Only checked when the file claims an artist. A filename-derived query often has none, and
        // demanding one would reject every track this feature exists to help.
        if (!string.IsNullOrWhiteSpace(query.Artist) && !Similar(query.Artist, candidate.Artist))
        {
            return MatchRejection.DifferentArtist;
        }

        return MatchRejection.None;
    }

    /// <summary>
    /// Whether two names refer to the same thing, once presentation is stripped away.
    /// </summary>
    /// <remarks>
    /// Containment rather than equality after normalising, because the two sides disagree in
    /// predictable, one-directional ways: a file is called "Song (Official Video)" or
    /// "Song - Remastered 2011" where the database says "Song". Requiring equality would reject
    /// most of a library assembled from downloads, which is the library this feature is for.
    /// </remarks>
    internal static bool Similar(string? left, string? right)
    {
        var a = Normalise(left);
        var b = Normalise(right);

        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        if (a == b)
        {
            return true;
        }

        // Whitespace is not part of a name's identity, and the two sides disagree about it
        // constantly: a downloaded file is called "selfcare" where the database says "Self Care",
        // and "NothingElseMatters" where it says "Nothing Else Matters". Compared without spaces
        // these are plainly the same title, and the duration check still has to agree separately.
        if (a.Replace(" ", "", StringComparison.Ordinal) == b.Replace(" ", "", StringComparison.Ordinal))
        {
            return true;
        }

        // The shorter must appear whole in the longer, and must be substantial. Without a length
        // floor, a track called "Go" would match anything containing "go".
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);

        return shorter.Length >= 4 && longer.Contains(shorter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lowercases, strips accents and punctuation, and collapses whitespace.
    /// </summary>
    /// <remarks>
    /// Also drops the bracketed suffixes that downloaded files accumulate -- "(Official Video)",
    /// "[HD]", "(Lyrics)" -- which are about the file rather than the recording and are exactly
    /// what stops an otherwise perfect title from matching.
    /// </remarks>
    internal static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value;

        // Bracketed noise, removed before anything else so the punctuation strip does not merge it
        // into the title.
        for (var i = 0; i < 4; i++)
        {
            var trimmed = RemoveOneBracketedGroup(text);

            if (trimmed == text)
            {
                break;
            }

            text = trimmed;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;

        foreach (var raw in text)
        {
            var ch = char.ToLowerInvariant(raw);

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(Fold(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Maps an accented lowercase letter to its unaccented form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An explicit table, because the obvious approach does not work here. Decomposing with
    /// <c>NormalizationForm.FormD</c> and dropping the combining marks is the standard way to fold
    /// diacritics -- and this project sets <c>InvariantGlobalization</c>, under which
    /// <c>string.Normalize</c> is a no-op. It does not throw and it does not warn; it returns the
    /// string unchanged, so the fold silently does nothing and "Motörhead" never matches
    /// "Motorhead". That is the sort of failure that ships.
    /// </para>
    /// <para>
    /// Latin only, and deliberately so. There is no sensible ASCII fold for Japanese or Cyrillic,
    /// and those scripts compare perfectly well as themselves -- the problem being solved is that
    /// one side of a comparison writes "Beyoncé" and the other writes "Beyonce".
    /// </para>
    /// </remarks>
    private static string Fold(char ch) => ch switch
    {
        'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' or 'ā' or 'ă' or 'ą' => "a",
        'ç' or 'ć' or 'ĉ' or 'ċ' or 'č' => "c",
        'ď' or 'đ' or 'ð' => "d",
        'è' or 'é' or 'ê' or 'ë' or 'ē' or 'ĕ' or 'ė' or 'ę' or 'ě' => "e",
        'ĝ' or 'ğ' or 'ġ' or 'ģ' => "g",
        'ĥ' or 'ħ' => "h",
        'ì' or 'í' or 'î' or 'ï' or 'ĩ' or 'ī' or 'ĭ' or 'į' or 'ı' => "i",
        'ĵ' => "j",
        'ķ' => "k",
        'ĺ' or 'ļ' or 'ľ' or 'ŀ' or 'ł' => "l",
        'ñ' or 'ń' or 'ņ' or 'ň' => "n",
        'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø' or 'ō' or 'ŏ' or 'ő' => "o",
        'ŕ' or 'ŗ' or 'ř' => "r",
        'ś' or 'ŝ' or 'ş' or 'š' => "s",
        'ţ' or 'ť' or 'ŧ' => "t",
        'ù' or 'ú' or 'û' or 'ü' or 'ũ' or 'ū' or 'ŭ' or 'ů' or 'ű' or 'ų' => "u",
        'ŵ' => "w",
        'ý' or 'ÿ' or 'ŷ' => "y",
        'ź' or 'ż' or 'ž' => "z",

        // The ligatures and the ones that expand to two letters. Folding these to a single
        // character would make "Mötley Crüe" work and "Encyclopædia" not.
        'æ' => "ae",
        'œ' => "oe",
        'ß' => "ss",
        'þ' => "th",

        _ => ch.ToString(),
    };

    /// <summary>Removes the first balanced () or [] group, if there is one.</summary>
    private static string RemoveOneBracketedGroup(string text)
    {
        foreach (var (open, close) in new[] { ('(', ')'), ('[', ']') })
        {
            var start = text.IndexOf(open);

            if (start < 0)
            {
                continue;
            }

            var end = text.IndexOf(close, start + 1);

            if (end < 0)
            {
                continue;
            }

            // Only if something survives. A title that is entirely bracketed is its own title, and
            // stripping it would leave nothing to match on.
            var without = (text[..start] + text[(end + 1)..]).Trim();

            if (without.Length > 0)
            {
                return without;
            }
        }

        return text;
    }
}
