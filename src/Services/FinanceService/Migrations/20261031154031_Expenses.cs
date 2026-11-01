using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class Expenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "expense_approvals",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExpenseId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_approvals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "expense_decisions",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExpenseId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PostingId = table.Column<long>(type: "bigint", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "expense_payments",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExpenseId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PaidBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PostingId = table.Column<long>(type: "bigint", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_payments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "expense_receipts",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExpenseId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
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
                    table.PrimaryKey("PK_expense_receipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "expenses",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Payee = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CategoryKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccountCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ProjectSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsReimbursement = table.Column<bool>(type: "boolean", nullable: false),
                    IncurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    RequiredApprovals = table.Column<int>(type: "integer", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubmittedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expenses", x => x.Id);
                    table.CheckConstraint("ck_expenses_positive", "\"Amount\" > 0 AND \"RequiredApprovals\" >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_expense_approvals_ExpenseId_ApprovedBy",
                schema: "finance",
                table: "expense_approvals",
                columns: new[] { "ExpenseId", "ApprovedBy" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_expense_decisions_ExpenseId",
                schema: "finance",
                table: "expense_decisions",
                column: "ExpenseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_expense_payments_ExpenseId",
                schema: "finance",
                table: "expense_payments",
                column: "ExpenseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_expense_receipts_ExpenseId",
                schema: "finance",
                table: "expense_receipts",
                column: "ExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_expenses_IncurredOn",
                schema: "finance",
                table: "expenses",
                column: "IncurredOn");

            migrationBuilder.CreateIndex(
                name: "IX_expenses_ProjectSlug",
                schema: "finance",
                table: "expenses",
                column: "ProjectSlug");

            // Expenses, their approvals, decision, receipts and payment are financial evidence: append-only in the database too (PostgreSQL only).
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER expenses_append_only BEFORE UPDATE OR DELETE ON finance.expenses
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER expense_receipts_append_only BEFORE UPDATE OR DELETE ON finance.expense_receipts
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER expense_approvals_append_only BEFORE UPDATE OR DELETE ON finance.expense_approvals
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER expense_decisions_append_only BEFORE UPDATE OR DELETE ON finance.expense_decisions
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER expense_payments_append_only BEFORE UPDATE OR DELETE ON finance.expense_payments
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER expenses_append_only ON finance.expenses; DROP TRIGGER expense_receipts_append_only ON finance.expense_receipts; DROP TRIGGER expense_approvals_append_only ON finance.expense_approvals; DROP TRIGGER expense_decisions_append_only ON finance.expense_decisions; DROP TRIGGER expense_payments_append_only ON finance.expense_payments;");
            }

            migrationBuilder.DropTable(
                name: "expense_approvals",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "expense_decisions",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "expense_payments",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "expense_receipts",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "expenses",
                schema: "finance");
        }
    }
}
