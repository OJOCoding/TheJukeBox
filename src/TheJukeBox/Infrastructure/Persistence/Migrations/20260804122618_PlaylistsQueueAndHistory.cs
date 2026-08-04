using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheJukeBox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlaylistsQueueAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastPlayedAtUtc",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlayCount",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PlayHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    SecondsPlayed = table.Column<int>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayHistory_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlayHistory_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaybackStates",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QueuedTrackIds = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: false),
                    Cursor = table.Column<int>(type: "INTEGER", nullable: false),
                    PositionSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Shuffle = table.Column<bool>(type: "INTEGER", nullable: false),
                    Repeat = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackStates", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_PlaybackStates_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Playlists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Playlists_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaylistItems_Playlists_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "Playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaylistItems_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Browse_LastPlayed",
                table: "Tracks",
                columns: new[] { "UserId", "DeletedAtUtc", "LastPlayedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Browse_PlayCount",
                table: "Tracks",
                columns: new[] { "UserId", "DeletedAtUtc", "PlayCount" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayHistory_TrackId",
                table: "PlayHistory",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayHistory_UserId_PlayedAtUtc",
                table: "PlayHistory",
                columns: new[] { "UserId", "PlayedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistItems_PlaylistId_Position",
                table: "PlaylistItems",
                columns: new[] { "PlaylistId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistItems_TrackId",
                table: "PlaylistItems",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_UserId_UpdatedAtUtc",
                table: "Playlists",
                columns: new[] { "UserId", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayHistory");

            migrationBuilder.DropTable(
                name: "PlaybackStates");

            migrationBuilder.DropTable(
                name: "PlaylistItems");

            migrationBuilder.DropTable(
                name: "Playlists");

            migrationBuilder.DropIndex(
                name: "IX_Tracks_Browse_LastPlayed",
                table: "Tracks");

            migrationBuilder.DropIndex(
                name: "IX_Tracks_Browse_PlayCount",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "LastPlayedAtUtc",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "PlayCount",
                table: "Tracks");
        }
    }
}
