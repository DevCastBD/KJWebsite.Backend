using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class PaymentAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_attempts",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DonationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Gateway = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_attempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_DonationId_Id",
                schema: "finance",
                table: "payment_attempts",
                columns: new[] { "DonationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_Gateway_ProviderPaymentId",
                schema: "finance",
                table: "payment_attempts",
                columns: new[] { "Gateway", "ProviderPaymentId" });

            // Payment attempts are part of the append-only ledger (PostgreSQL only), using the function from the first migration.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER payment_attempts_append_only BEFORE UPDATE OR DELETE ON finance.payment_attempts
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER payment_attempts_append_only ON finance.payment_attempts;");
            }

            migrationBuilder.DropTable(
                name: "payment_attempts",
                schema: "finance");
        }
    }
}
