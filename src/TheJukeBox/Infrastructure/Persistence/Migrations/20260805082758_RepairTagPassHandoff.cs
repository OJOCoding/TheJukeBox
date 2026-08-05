using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheJukeBox.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Hands every never-actually-examined track back to the tag pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A data repair, not a schema change. The reconciler used to stamp
    /// <c>MetadataExtractedAtUtc</c> and <c>MetadataFingerprint</c> when it wrote a filename guess,
    /// but those columns mean "the tag pass has read these bytes" -- so every freshly synced track
    /// looked already-examined, the tag pass found nothing to do, and no library ever received a
    /// duration or a cover image. See <c>LibrarySynchroniser.Apply</c> and
    /// <c>SyncEnricherHandoffTests</c>.
    /// </para>
    /// <para>
    /// Fixing the code is not enough on its own: existing rows still carry the false stamp, and
    /// nothing would ever clear it, because the reconciler only clears it when a file's content
    /// changes. Without this, the bug is fixed for libraries synced from now on and permanent for
    /// every library that already exists.
    /// </para>
    /// <para>
    /// Rows whose metadata genuinely came from tags are left alone -- they were examined, and
    /// re-reading them would download the front of every one of those files again for nothing.
    /// Rows that are filename-derived or have no metadata at all are cleared, so the next sync
    /// reads them. A file that truly has no tags is read once more and then stamped for real by the
    /// enricher, which terminates.
    /// </para>
    /// </remarks>
    public partial class RepairTagPassHandoff : Migration
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
            // Deliberately empty. The forward direction discards a value that was wrong, so there
            // is nothing meaningful to restore -- and re-stamping these rows would reintroduce the
            // bug for anyone who rolled back.
        }
    }
}
