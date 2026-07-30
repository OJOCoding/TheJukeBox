using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheJukeBox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumArt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlbumArt",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumArt", x => x.Sha256);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumArt");
        }
    }
}
