using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class DonorProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AccountShared",
                schema: "finance",
                table: "donors",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AccountStatus",
                schema: "finance",
                table: "donors",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<string>(
                name: "AccountSubject",
                schema: "finance",
                table: "donors",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressProtected",
                schema: "finance",
                table: "donors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                schema: "finance",
                table: "donors",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailKey",
                schema: "finance",
                table: "donors",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NotesProtected",
                schema: "finance",
                table: "donors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Organisation",
                schema: "finance",
                table: "donors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "finance",
                table: "donors",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<string>(
                name: "Type",
                schema: "finance",
                table: "donors",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "individual");

            migrationBuilder.AddColumn<int>(
                name: "Version",
                schema: "finance",
                table: "donors",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Existing donors were keyed by the hash of their email, so their lookup key is the key they already have. This must run before the
            // unique index on EmailKey is built: every row would otherwise share the empty default.
            migrationBuilder.Sql(@"UPDATE finance.donors SET ""EmailKey"" = ""DonorKey"" WHERE ""EmailKey"" = '';");

            migrationBuilder.CreateIndex(
                name: "IX_donors_AccountSubject",
                schema: "finance",
                table: "donors",
                column: "AccountSubject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_donors_EmailKey",
                schema: "finance",
                table: "donors",
                column: "EmailKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_donors_AccountSubject",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropIndex(
                name: "IX_donors_EmailKey",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "AccountShared",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "AccountStatus",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "AccountSubject",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "AddressProtected",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "Country",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "EmailKey",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "NotesProtected",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "Organisation",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "Type",
                schema: "finance",
                table: "donors");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "finance",
                table: "donors");
        }
    }
}
