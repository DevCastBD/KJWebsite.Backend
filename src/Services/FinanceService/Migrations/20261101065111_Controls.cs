using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class Controls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "close_events",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Scope = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    By = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_close_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "control_requests",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Subject = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_control_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "control_decisions",
                schema: "finance",
                columns: table => new
                {
                    RequestId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ResultRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_control_decisions", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_control_decisions_control_requests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "finance",
                        principalTable: "control_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_close_events_Scope_Key",
                schema: "finance",
                table: "close_events",
                columns: new[] { "Scope", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_control_requests_Kind_Subject",
                schema: "finance",
                table: "control_requests",
                columns: new[] { "Kind", "Subject" });

            // PostgreSQL guards (like the other append-only records). Requests, decisions and close events cannot be edited or removed. An approval or rejection must come from someone
            // other than the requester and a withdrawal from the requester. A close may be edited never and deleted only when an approved reopen by a different person is on record.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER control_requests_append_only BEFORE UPDATE OR DELETE ON finance.control_requests
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER control_decisions_append_only BEFORE UPDATE OR DELETE ON finance.control_decisions
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER close_events_append_only BEFORE UPDATE OR DELETE ON finance.close_events
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE FUNCTION finance.check_control_decision() RETURNS trigger LANGUAGE plpgsql AS $fn$
                    DECLARE requester text;
                    BEGIN
                        SELECT "RequestedBy" INTO requester FROM finance.control_requests WHERE "Id" = NEW."RequestId";
                        IF NEW."Decision" IN ('approved', 'rejected') AND NEW."DecidedBy" = requester THEN
                            RAISE EXCEPTION 'a request cannot be % by the person who made it', NEW."Decision" USING ERRCODE = '23514';
                        END IF;
                        IF NEW."Decision" = 'withdrawn' AND NEW."DecidedBy" <> requester THEN
                            RAISE EXCEPTION 'a request can only be withdrawn by the person who made it' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END;
                    $fn$;
                    CREATE TRIGGER control_decisions_distinct_people BEFORE INSERT ON finance.control_decisions
                        FOR EACH ROW EXECUTE FUNCTION finance.check_control_decision();

                    DROP TRIGGER period_closes_append_only ON finance.period_closes;
                    DROP TRIGGER fiscal_year_closes_append_only ON finance.fiscal_year_closes;
                    CREATE TRIGGER period_closes_no_update BEFORE UPDATE ON finance.period_closes
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER fiscal_year_closes_no_update BEFORE UPDATE ON finance.fiscal_year_closes
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE FUNCTION finance.refuse_close_delete_without_reopen() RETURNS trigger LANGUAGE plpgsql AS $fn$
                    DECLARE scope text := TG_ARGV[0];
                    DECLARE k text;
                    BEGIN
                        IF scope = 'period' THEN k := OLD."PeriodKey"; ELSE k := OLD."Label"; END IF;
                        IF NOT EXISTS (
                            SELECT 1 FROM finance.close_events e
                            WHERE e."Scope" = scope AND e."Key" = k AND e."Kind" = 'reopened' AND e."ApprovedBy" IS NOT NULL AND e."ApprovedBy" <> e."By"
                              AND e."Id" > COALESCE((SELECT max(c."Id") FROM finance.close_events c WHERE c."Scope" = scope AND c."Key" = k AND c."Kind" = 'closed'), 0)) THEN
                            RAISE EXCEPTION 'a close can only be removed by an approved reopen' USING ERRCODE = '42501';
                        END IF;
                        RETURN OLD;
                    END;
                    $fn$;
                    CREATE TRIGGER period_closes_reopen_only BEFORE DELETE ON finance.period_closes
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_close_delete_without_reopen('period');
                    CREATE TRIGGER fiscal_year_closes_reopen_only BEFORE DELETE ON finance.fiscal_year_closes
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_close_delete_without_reopen('year');
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql(@"DROP TRIGGER fiscal_year_closes_reopen_only ON finance.fiscal_year_closes; DROP TRIGGER period_closes_reopen_only ON finance.period_closes; DROP FUNCTION finance.refuse_close_delete_without_reopen();
                    DROP TRIGGER fiscal_year_closes_no_update ON finance.fiscal_year_closes; DROP TRIGGER period_closes_no_update ON finance.period_closes;
                    CREATE TRIGGER period_closes_append_only BEFORE UPDATE OR DELETE ON finance.period_closes FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER fiscal_year_closes_append_only BEFORE UPDATE OR DELETE ON finance.fiscal_year_closes FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    DROP TRIGGER control_decisions_distinct_people ON finance.control_decisions; DROP FUNCTION finance.check_control_decision();
                    DROP TRIGGER close_events_append_only ON finance.close_events; DROP TRIGGER control_decisions_append_only ON finance.control_decisions; DROP TRIGGER control_requests_append_only ON finance.control_requests;");
            }

            migrationBuilder.DropTable(
                name: "close_events",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "control_decisions",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "control_requests",
                schema: "finance");
        }
    }
}
