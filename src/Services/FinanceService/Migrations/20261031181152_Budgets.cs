using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class Budgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budget_alerts",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BudgetId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ThresholdPercent = table.Column<int>(type: "integer", nullable: false),
                    PlannedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ActualAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "budgets",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FiscalYear = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ProjectSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CategoryKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PlannedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budgets", x => x.Id);
                    table.CheckConstraint("ck_budgets_positive", "\"PlannedAmount\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "fiscal_year_closes",
                schema: "finance",
                columns: table => new
                {
                    Label = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_year_closes", x => x.Label);
                });

            migrationBuilder.CreateIndex(
                name: "IX_budget_alerts_BudgetId_ThresholdPercent",
                schema: "finance",
                table: "budget_alerts",
                columns: new[] { "BudgetId", "ThresholdPercent" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_budgets_FiscalYear_Currency_CategoryKey",
                schema: "finance",
                table: "budgets",
                columns: new[] { "FiscalYear", "Currency", "CategoryKey" },
                unique: true,
                filter: "\"ProjectSlug\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_budgets_FiscalYear_Currency_ProjectSlug_CategoryKey",
                schema: "finance",
                table: "budgets",
                columns: new[] { "FiscalYear", "Currency", "ProjectSlug", "CategoryKey" },
                unique: true,
                filter: "\"ProjectSlug\" IS NOT NULL");

            // A closed fiscal year takes no posting from any tool, and closes and alerts are append-only (PostgreSQL only).
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER fiscal_year_closes_append_only BEFORE UPDATE OR DELETE ON finance.fiscal_year_closes
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER budget_alerts_append_only BEFORE UPDATE OR DELETE ON finance.budget_alerts
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();

                    CREATE FUNCTION finance.refuse_closed_fiscal_year() RETURNS trigger LANGUAGE plpgsql AS $fn$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM finance.fiscal_year_closes WHERE "Label" = NEW."FiscalYear") THEN
                            RAISE EXCEPTION 'fiscal year % is closed', NEW."FiscalYear" USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END;
                    $fn$;
                    CREATE TRIGGER postings_fiscal_year_open BEFORE INSERT ON finance.postings
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_closed_fiscal_year();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER postings_fiscal_year_open ON finance.postings; DROP FUNCTION finance.refuse_closed_fiscal_year(); DROP TRIGGER budget_alerts_append_only ON finance.budget_alerts; DROP TRIGGER fiscal_year_closes_append_only ON finance.fiscal_year_closes;");
            }

            migrationBuilder.DropTable(
                name: "budget_alerts",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "budgets",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "fiscal_year_closes",
                schema: "finance");
        }
    }
}
