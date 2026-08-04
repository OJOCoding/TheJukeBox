using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheJukeBox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Pending" rather than the scaffolded "": an empty string is not a UserStatus, so
            // reading such a row back would throw on the enum conversion. Pending is also the
            // right default for any future insert that bypasses EF -- closed, not open.
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Users",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Pending");

            // Backfill. Every account that exists at this point was created before approval
            // was a concept and has been using the instance already, so it is activated.
            // Without this, upgrading would silently lock out every existing user -- including
            // the operator, who would then have no administrator account to approve anyone
            // with. Only rows present now are touched; later sign-ups get their status from
            // the registration mode.
            migrationBuilder.Sql("UPDATE Users SET Status = 'Active';");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Status",
                table: "Users",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Status",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Users");
        }
    }
}
