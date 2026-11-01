using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class StripePayPal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_settlements",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DonationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Gateway = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SettledAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SettledCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Fee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_settlements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "provider_notices",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Gateway = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EventKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DonationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_notices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_settlements_DonationId",
                schema: "finance",
                table: "payment_settlements",
                column: "DonationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_settlements_Gateway_TransactionId",
                schema: "finance",
                table: "payment_settlements",
                columns: new[] { "Gateway", "TransactionId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_notices_DonationId",
                schema: "finance",
                table: "provider_notices",
                column: "DonationId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_notices_Gateway_EventKey",
                schema: "finance",
                table: "provider_notices",
                columns: new[] { "Gateway", "EventKey" },
                unique: true);

            // Settlements and provider notices are evidence: append-only in the database too (PostgreSQL only).
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER payment_settlements_append_only BEFORE UPDATE OR DELETE ON finance.payment_settlements
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER provider_notices_append_only BEFORE UPDATE OR DELETE ON finance.provider_notices
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER payment_settlements_append_only ON finance.payment_settlements; DROP TRIGGER provider_notices_append_only ON finance.provider_notices;");
            }

            migrationBuilder.DropTable(
                name: "payment_settlements",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "provider_notices",
                schema: "finance");
        }
    }
}
