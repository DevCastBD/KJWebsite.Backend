using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class DonorCrm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CommunicationConsent",
                schema: "finance",
                table: "donations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "donor_consents",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DonorId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Granted = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_donor_consents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "donor_exports",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Segment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AsOf = table.Column<DateOnly>(type: "date", nullable: false),
                    ConsentFilter = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Rows = table.Column<int>(type: "integer", nullable: false),
                    ExportedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_donor_exports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "donor_suppressions",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DonorId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Template = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_donor_suppressions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "donors",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DonorKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NameProtected = table.Column<string>(type: "text", nullable: false),
                    EmailProtected = table.Column<string>(type: "text", nullable: false),
                    PhoneProtected = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_donors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_donor_consents_DonorId_Purpose_Id",
                schema: "finance",
                table: "donor_consents",
                columns: new[] { "DonorId", "Purpose", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_donor_suppressions_DonorId",
                schema: "finance",
                table: "donor_suppressions",
                column: "DonorId");

            migrationBuilder.CreateIndex(
                name: "IX_donors_DonorKey",
                schema: "finance",
                table: "donors",
                column: "DonorKey",
                unique: true);

            // Consent history, suppressions and the export log are evidence: append-only in the database too (PostgreSQL only). Donor rows themselves stay editable.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER donor_consents_append_only BEFORE UPDATE OR DELETE ON finance.donor_consents
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER donor_suppressions_append_only BEFORE UPDATE OR DELETE ON finance.donor_suppressions
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER donor_exports_append_only BEFORE UPDATE OR DELETE ON finance.donor_exports
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER donor_consents_append_only ON finance.donor_consents; DROP TRIGGER donor_suppressions_append_only ON finance.donor_suppressions; DROP TRIGGER donor_exports_append_only ON finance.donor_exports;");
            }

            migrationBuilder.DropTable(
                name: "donor_consents",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "donor_exports",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "donor_suppressions",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "donors",
                schema: "finance");

            migrationBuilder.DropColumn(
                name: "CommunicationConsent",
                schema: "finance",
                table: "donations");
        }
    }
}
