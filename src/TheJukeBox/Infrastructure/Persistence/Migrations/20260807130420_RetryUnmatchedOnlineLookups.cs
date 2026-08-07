using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheJukeBox.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Forgets online lookups that found nothing, so the improved query gets a turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first version of the lookup constrained every search by the track's album tag. That is
    /// right when the tag names an album and useless when it names something else -- and for a
    /// downloaded library it very often names the folder the files were ripped into. Searching for
    /// a recording within a release called "Darien Çepani 2-6" returns nothing, however famous the
    /// song.
    /// </para>
    /// <para>
    /// Those misses were then remembered, which is exactly what the negative cache is for and
    /// exactly the wrong outcome here: the answers were wrong for a reason that has since been
    /// fixed, and without this nobody would ask again for a month.
    /// </para>
    /// <para>
    /// Only unmatched tracks are cleared. A track that did match keeps its result, so no successful
    /// lookup is repeated and no rate budget is spent re-confirming it.
    /// </para>
    /// </remarks>
    public partial class RetryUnmatchedOnlineLookups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Tracks
                SET OnlineLookupAtUtc = NULL
                WHERE MusicBrainzRecordingId IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the forward direction discards a "we asked and got nothing"
            // marker that was produced by a query since replaced.
        }
    }
}
