using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class Ledger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "period_closes",
                schema: "finance",
                columns: table => new
                {
                    PeriodKey = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_period_closes", x => x.PeriodKey);
                });

            migrationBuilder.CreateTable(
                name: "postings",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PostedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    FiscalYear = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReversesPostingId = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_postings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
                    AccountId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Debit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_entries", x => x.Id);
                    table.CheckConstraint("ck_ledger_entries_one_side", "\"Debit\" >= 0 AND \"Credit\" >= 0 AND ((\"Debit\" > 0 AND \"Credit\" = 0) OR (\"Debit\" = 0 AND \"Credit\" > 0))");
                    table.ForeignKey(
                        name: "FK_ledger_entries_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "finance",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ledger_entries_postings_PostingId",
                        column: x => x.PostingId,
                        principalSchema: "finance",
                        principalTable: "postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Code",
                schema: "finance",
                table: "accounts",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_AccountId_PostingId",
                schema: "finance",
                table: "ledger_entries",
                columns: new[] { "AccountId", "PostingId" });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_PostingId",
                schema: "finance",
                table: "ledger_entries",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_postings_PeriodKey",
                schema: "finance",
                table: "postings",
                column: "PeriodKey");

            migrationBuilder.CreateIndex(
                name: "IX_postings_PostedOn",
                schema: "finance",
                table: "postings",
                column: "PostedOn");

            migrationBuilder.CreateIndex(
                name: "IX_postings_ReversesPostingId",
                schema: "finance",
                table: "postings",
                column: "ReversesPostingId",
                unique: true,
                filter: "\"ReversesPostingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_postings_SourceType_SourceId",
                schema: "finance",
                table: "postings",
                columns: new[] { "SourceType", "SourceId" },
                unique: true,
                filter: "\"SourceId\" IS NOT NULL");

            // The ledger's rules in the database too (PostgreSQL only), so no statement from any tool can break them: postings, lines and period closes are
            // append-only; accounts are never deleted and keep their code and type; a posting needs two or more lines that balance in every currency
            // (checked at commit, after all its lines are in); and a closed period takes no new postings.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER postings_append_only BEFORE UPDATE OR DELETE ON finance.postings
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER ledger_entries_append_only BEFORE UPDATE OR DELETE ON finance.ledger_entries
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER period_closes_append_only BEFORE UPDATE OR DELETE ON finance.period_closes
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();

                    CREATE FUNCTION finance.protect_account() RETURNS trigger LANGUAGE plpgsql AS $fn$
                    BEGIN
                        IF TG_OP = 'DELETE' THEN
                            RAISE EXCEPTION 'accounts cannot be deleted' USING ERRCODE = '42501';
                        END IF;
                        IF NEW."Code" IS DISTINCT FROM OLD."Code" OR NEW."Type" IS DISTINCT FROM OLD."Type" THEN
                            RAISE EXCEPTION 'an account''s code and type cannot change' USING ERRCODE = '42501';
                        END IF;
                        RETURN NEW;
                    END;
                    $fn$;
                    CREATE TRIGGER accounts_protect BEFORE UPDATE OR DELETE ON finance.accounts
                        FOR EACH ROW EXECUTE FUNCTION finance.protect_account();

                    CREATE FUNCTION finance.check_posting_complete() RETURNS trigger LANGUAGE plpgsql AS $fn$
                    DECLARE pid bigint;
                    BEGIN
                        IF TG_TABLE_NAME = 'postings' THEN pid := NEW."Id"; ELSE pid := NEW."PostingId"; END IF;
                        IF (SELECT count(*) FROM finance.ledger_entries WHERE "PostingId" = pid) < 2 THEN
                            RAISE EXCEPTION 'posting % needs at least two lines', pid USING ERRCODE = '23514';
                        END IF;
                        IF EXISTS (SELECT 1 FROM finance.ledger_entries WHERE "PostingId" = pid GROUP BY "Currency" HAVING SUM("Debit") <> SUM("Credit")) THEN
                            RAISE EXCEPTION 'posting % is unbalanced: debits must equal credits in every currency', pid USING ERRCODE = '23514';
                        END IF;
                        RETURN NULL;
                    END;
                    $fn$;
                    CREATE CONSTRAINT TRIGGER postings_complete AFTER INSERT ON finance.postings
                        DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION finance.check_posting_complete();
                    CREATE CONSTRAINT TRIGGER ledger_entries_balanced AFTER INSERT ON finance.ledger_entries
                        DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION finance.check_posting_complete();

                    CREATE FUNCTION finance.refuse_closed_period() RETURNS trigger LANGUAGE plpgsql AS $fn$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM finance.period_closes WHERE "PeriodKey" = NEW."PeriodKey") THEN
                            RAISE EXCEPTION 'period % is closed', NEW."PeriodKey" USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END;
                    $fn$;
                    CREATE TRIGGER postings_period_open BEFORE INSERT ON finance.postings
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_closed_period();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    DROP TRIGGER postings_period_open ON finance.postings;
                    DROP TRIGGER ledger_entries_balanced ON finance.ledger_entries;
                    DROP TRIGGER postings_complete ON finance.postings;
                    DROP TRIGGER accounts_protect ON finance.accounts;
                    DROP TRIGGER period_closes_append_only ON finance.period_closes;
                    DROP TRIGGER ledger_entries_append_only ON finance.ledger_entries;
                    DROP TRIGGER postings_append_only ON finance.postings;
                    DROP FUNCTION finance.refuse_closed_period();
                    DROP FUNCTION finance.check_posting_complete();
                    DROP FUNCTION finance.protect_account();
                    """);
            }

            migrationBuilder.DropTable(
                name: "ledger_entries",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "period_closes",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "postings",
                schema: "finance");
        }
    }
}
