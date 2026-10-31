using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class OfflineDonations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "offline_decisions",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OfflineRecordId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offline_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "offline_proofs",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OfflineRecordId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UploadedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetainUntil = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offline_proofs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "offline_records",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DonationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ReferenceNo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ItemDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ValuationBasis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ValuationNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offline_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_offline_decisions_OfflineRecordId",
                schema: "finance",
                table: "offline_decisions",
                column: "OfflineRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offline_proofs_OfflineRecordId",
                schema: "finance",
                table: "offline_proofs",
                column: "OfflineRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_offline_records_DonationId",
                schema: "finance",
                table: "offline_records",
                column: "DonationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offline_records_ReceivedOn",
                schema: "finance",
                table: "offline_records",
                column: "ReceivedOn");

            // Offline records, their decisions and proofs are financial evidence: append-only in the database too (PostgreSQL only).
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER offline_records_append_only BEFORE UPDATE OR DELETE ON finance.offline_records
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER offline_decisions_append_only BEFORE UPDATE OR DELETE ON finance.offline_decisions
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER offline_proofs_append_only BEFORE UPDATE OR DELETE ON finance.offline_proofs
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER offline_records_append_only ON finance.offline_records; DROP TRIGGER offline_decisions_append_only ON finance.offline_decisions; DROP TRIGGER offline_proofs_append_only ON finance.offline_proofs;");
            }

            migrationBuilder.DropTable(
                name: "offline_decisions",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "offline_proofs",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "offline_records",
                schema: "finance");
        }
    }
}
