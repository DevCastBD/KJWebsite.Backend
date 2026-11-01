using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class Receipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_outbox",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Template = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    RecipientProtected = table.Column<string>(type: "text", nullable: false),
                    PayloadProtected = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "receipt_reissues",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReceiptId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ReissuedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt_reissues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "receipt_voids",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReceiptId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    VoidedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt_voids", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "receipts",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DonationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Series = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PrevHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    DonorNameProtected = table.Column<string>(type: "text", nullable: false),
                    DonorEmailProtected = table.Column<string>(type: "text", nullable: false),
                    DonorKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FeeAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DesignationType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DesignationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsAnonymous = table.Column<bool>(type: "boolean", nullable: false),
                    Language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    IsInKind = table.Column<bool>(type: "boolean", nullable: false),
                    ItemDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ValuationBasis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    OrganisationJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "statement_sends",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DonorKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FiscalYear = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    QueuedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_statement_sends", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_outbox_SentAt",
                schema: "finance",
                table: "notification_outbox",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_reissues_ReceiptId",
                schema: "finance",
                table: "receipt_reissues",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_voids_ReceiptId",
                schema: "finance",
                table: "receipt_voids",
                column: "ReceiptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receipts_DonationId",
                schema: "finance",
                table: "receipts",
                column: "DonationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receipts_Number",
                schema: "finance",
                table: "receipts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receipts_Series_DonorKey",
                schema: "finance",
                table: "receipts",
                columns: new[] { "Series", "DonorKey" });

            migrationBuilder.CreateIndex(
                name: "IX_receipts_Series_Seq",
                schema: "finance",
                table: "receipts",
                columns: new[] { "Series", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_statement_sends_DonorKey_FiscalYear",
                schema: "finance",
                table: "statement_sends",
                columns: new[] { "DonorKey", "FiscalYear" },
                unique: true);

            // Receipts, voids, re-issues and statement queueing are append-only in the database too (PostgreSQL only): no number can be removed or changed.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER receipts_append_only BEFORE UPDATE OR DELETE ON finance.receipts
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER receipt_voids_append_only BEFORE UPDATE OR DELETE ON finance.receipt_voids
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER receipt_reissues_append_only BEFORE UPDATE OR DELETE ON finance.receipt_reissues
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER statement_sends_append_only BEFORE UPDATE OR DELETE ON finance.statement_sends
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER receipts_append_only ON finance.receipts; DROP TRIGGER receipt_voids_append_only ON finance.receipt_voids; DROP TRIGGER receipt_reissues_append_only ON finance.receipt_reissues; DROP TRIGGER statement_sends_append_only ON finance.statement_sends;");
            }

            migrationBuilder.DropTable(
                name: "notification_outbox",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "receipt_reissues",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "receipt_voids",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "receipts",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "statement_sends",
                schema: "finance");
        }
    }
}
