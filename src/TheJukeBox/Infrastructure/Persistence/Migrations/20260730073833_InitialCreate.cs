using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheJukeBox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GoogleSubjectId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PictureUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DriveSyncStates",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChangesStartPageToken = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ItemsProcessed = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsTotal = table.Column<int>(type: "INTEGER", nullable: false),
                    LastFullSyncAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    LastIncrementalSyncAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastErrorType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LeaseUntilUtc = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveSyncStates", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_DriveSyncStates_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriveTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EncryptedRefreshToken = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    GrantedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    LastRefreshedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveTokens", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_DriveTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_GoogleSubjectId",
                table: "Users",
                column: "GoogleSubjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriveSyncStates");

            migrationBuilder.DropTable(
                name: "DriveTokens");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
