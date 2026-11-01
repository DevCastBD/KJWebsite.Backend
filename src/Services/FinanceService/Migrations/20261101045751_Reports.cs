using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class Reports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "report_packs",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    EntityName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GeneratedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_packs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_packs_Month",
                schema: "finance",
                table: "report_packs",
                column: "Month");

            // A stored pack is evidence and append-only (PostgreSQL only, like the other guards).
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER report_packs_append_only BEFORE UPDATE OR DELETE ON finance.report_packs
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER report_packs_append_only ON finance.report_packs;");
            }

            migrationBuilder.DropTable(
                name: "report_packs",
                schema: "finance");
        }
    }
}
