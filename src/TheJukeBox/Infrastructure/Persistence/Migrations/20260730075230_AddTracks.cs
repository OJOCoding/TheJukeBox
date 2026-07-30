using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheJukeBox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DriveFileId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Md5Checksum = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    HeadRevisionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ParentFolderId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ParentFolderName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    DriveCreatedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    DriveModifiedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    FileExtension = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Album = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    AlbumArtist = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    TrackNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    DiscNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Genre = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    MetadataSource = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MetadataFingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MetadataExtractedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    ArtSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SearchText = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    AddedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeletedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tracks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Browse_Added",
                table: "Tracks",
                columns: new[] { "UserId", "DeletedAtUtc", "AddedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Browse_Album",
                table: "Tracks",
                columns: new[] { "UserId", "DeletedAtUtc", "Album", "DiscNumber", "TrackNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Browse_Artist",
                table: "Tracks",
                columns: new[] { "UserId", "DeletedAtUtc", "Artist" });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Browse_Title",
                table: "Tracks",
                columns: new[] { "UserId", "DeletedAtUtc", "Title" });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_RowVersion",
                table: "Tracks",
                columns: new[] { "UserId", "RowVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_UserId_DriveFileId",
                table: "Tracks",
                columns: new[] { "UserId", "DriveFileId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tracks");
        }
    }
}
