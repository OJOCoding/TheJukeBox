using System.ComponentModel.DataAnnotations;

namespace TheJukeBox.Configuration;

/// <summary>
/// Looking up metadata this instance could not find inside the files themselves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default, and it has to be.</b> A lookup sends the artist and title of a track in
/// someone's private library to a third party. That is a small disclosure and a completely
/// reasonable one to want, but it is not a decision this application gets to make on an
/// operator's behalf -- particularly one hosting other people's libraries, who would be making
/// it on their users' behalf in turn.
/// </para>
/// <para>
/// <b>It only ever fills gaps.</b> Tags embedded in a file are what its owner chose. An online
/// match is a guess made from a filename, so it is allowed to supply what is missing and never to
/// replace what is there.
/// </para>
/// </remarks>
public sealed class OnlineMetadataOptions
{
    public const string SectionName = "Metadata:Online";

    public bool Enabled { get; init; }

    /// <summary>
    /// A contact address, sent in the User-Agent.
    /// </summary>
    /// <remarks>
    /// Not optional politeness: MusicBrainz's terms require an application name, a version and a
    /// contact in the User-Agent, and they block clients that send a generic one. Enabling lookups
    /// without supplying this is a configuration error rather than something to paper over, so
    /// startup validation rejects it.
    /// </remarks>
    public string ContactEmail { get; init; } = "";

    /// <summary>
    /// How many tracks one pass will look up.
    /// </summary>
    /// <remarks>
    /// The service permits one request per second, so a pass is bounded by wall-clock time far
    /// more than by anything else: 200 tracks is around seven minutes of polite querying. Capping
    /// it keeps a first sync of a large library from spending hours on a background thread, and
    /// the remainder is simply picked up by the next pass.
    /// </remarks>
    [Range(1, 5000)]
    public int MaxTracksPerPass { get; init; } = 200;

    /// <summary>
    /// The lowest MusicBrainz relevance score this will accept, out of 100.
    /// </summary>
    /// <remarks>
    /// Deliberately high. Showing the wrong cover on someone's album is worse than showing no
    /// cover: the missing one reads as "not found yet", the wrong one reads as a broken app, and
    /// nobody goes looking for a setting to correct it.
    /// </remarks>
    [Range(0, 100)]
    public int MinimumScore { get; init; } = 90;

    /// <summary>
    /// How far a candidate's length may differ from the file's, in seconds.
    /// </summary>
    /// <remarks>
    /// The strongest signal available. Titles and artists are fuzzy strings that collide across
    /// covers, live versions and remasters, but a duration is a measurement -- if it disagrees by
    /// more than a few seconds it is a different recording, whatever the text says.
    /// </remarks>
    [Range(0, 120)]
    public int DurationToleranceSeconds { get; init; } = 8;

    /// <summary>
    /// How long before a track that found no match is tried again.
    /// </summary>
    /// <remarks>
    /// A negative result has to be remembered, or every sync re-queries every unmatched track
    /// forever at one request per second. It is re-tried eventually because the database grows:
    /// something unfindable today may be catalogued next year.
    /// </remarks>
    [Range(1, 3650)]
    public int RetryAfterDays { get; init; } = 30;
}
