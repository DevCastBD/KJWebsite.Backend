using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthIdentityService.Migrations
{
    /// <inheritdoc />
    public partial class ProvisionedFor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProvisionedFor",
                schema: "auth",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_ProvisionedFor",
                schema: "auth",
                table: "users",
                column: "ProvisionedFor",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_ProvisionedFor",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ProvisionedFor",
                schema: "auth",
                table: "users");
        }
    }
}
