using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class Webhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_events",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Gateway = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EventKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DonationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payouts",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Gateway = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayoutRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ImportedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_rejections",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Gateway = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_rejections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payout_lines",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayoutId = table.Column<long>(type: "bigint", nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DonationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Gross = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Fee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Net = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    MatchStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PostingId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payout_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payout_lines_payouts_PayoutId",
                        column: x => x.PayoutId,
                        principalSchema: "finance",
                        principalTable: "payouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payout_lines_postings_PostingId",
                        column: x => x.PostingId,
                        principalSchema: "finance",
                        principalTable: "postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_events_Gateway_EventKey",
                schema: "finance",
                table: "payment_events",
                columns: new[] { "Gateway", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payout_lines_DonationId",
                schema: "finance",
                table: "payout_lines",
                column: "DonationId",
                unique: true,
                filter: "\"MatchStatus\" = 'matched'");

            migrationBuilder.CreateIndex(
                name: "IX_payout_lines_PayoutId",
                schema: "finance",
                table: "payout_lines",
                column: "PayoutId");

            migrationBuilder.CreateIndex(
                name: "IX_payout_lines_PostingId",
                schema: "finance",
                table: "payout_lines",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_payouts_Gateway_PayoutRef",
                schema: "finance",
                table: "payouts",
                columns: new[] { "Gateway", "PayoutRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_rejections_Id",
                schema: "finance",
                table: "webhook_rejections",
                column: "Id");

            // Gateway events, rejections and payouts are evidence: append-only in the database too (PostgreSQL only).
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER payment_events_append_only BEFORE UPDATE OR DELETE ON finance.payment_events
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER webhook_rejections_append_only BEFORE UPDATE OR DELETE ON finance.webhook_rejections
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER payouts_append_only BEFORE UPDATE OR DELETE ON finance.payouts
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER payout_lines_append_only BEFORE UPDATE OR DELETE ON finance.payout_lines
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER payment_events_append_only ON finance.payment_events; DROP TRIGGER webhook_rejections_append_only ON finance.webhook_rejections; DROP TRIGGER payouts_append_only ON finance.payouts; DROP TRIGGER payout_lines_append_only ON finance.payout_lines;");
            }

            migrationBuilder.DropTable(
                name: "payment_events",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "payout_lines",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "webhook_rejections",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "payouts",
                schema: "finance");
        }
    }
}
