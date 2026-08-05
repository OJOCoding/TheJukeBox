using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheJukeBox.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Gives the tag pass one more attempt at tracks it could not read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second data repair, for a second bug in the same handoff. <c>DriveAccessToken</c> cached the
    /// access token but not the fetch, so the first few workers of a parallel pass all obtained the
    /// token at once -- and obtaining it reads the request's <c>DbContext</c>, which is not
    /// thread-safe. EF threw "a second operation was started on this context instance" and those
    /// files fell back to their file names.
    /// </para>
    /// <para>
    /// The count of casualties was exactly the degree of parallelism: four files per library,
    /// whichever four happened to start first. They kept a filename-derived title and lost their
    /// duration and cover art, and because the enricher stamps a file as examined even when
    /// extraction fails, nothing would ever try them again.
    /// </para>
    /// <para>
    /// Clearing only filename-derived rows means this costs one re-read of the files that have no
    /// tags anyway, and leaves every successfully tagged track untouched.
    /// </para>
    /// </remarks>
    public partial class RetryFilenameFallbackTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Tracks
                SET MetadataExtractedAtUtc = NULL,
                    MetadataFingerprint = NULL
                WHERE MetadataSource <> 'Tags';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the forward direction only discards a "we looked at this" marker
            // for files the pass demonstrably failed to read.
        }
    }
}
