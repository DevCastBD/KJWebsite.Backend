using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class InitialDonations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "finance");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Service = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Entity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Before = table.Column<string>(type: "text", nullable: true),
                    After = table.Column<string>(type: "text", nullable: true),
                    Operation = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RequestId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "donations",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reference = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FeeAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DesignationType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DesignationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsAnonymous = table.Column<bool>(type: "boolean", nullable: false),
                    Dedication = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DonorNameProtected = table.Column<string>(type: "text", nullable: false),
                    DonorEmailProtected = table.Column<string>(type: "text", nullable: false),
                    DonorPhoneProtected = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    RecurringPlanId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ReversesDonationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_donations", x => x.Id);
                    table.CheckConstraint("ck_donations_kind_sign", "(\"Kind\" = 'reversal' AND \"Amount\" < 0 AND \"FeeAmount\" <= 0) OR (\"Kind\" <> 'reversal' AND \"Amount\" > 0 AND \"FeeAmount\" >= 0)");
                });

            migrationBuilder.CreateTable(
                name: "recurring_plans",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Interval = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FeeAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DesignationType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DesignationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsAnonymous = table.Column<bool>(type: "boolean", nullable: false),
                    Dedication = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DonorNameProtected = table.Column<string>(type: "text", nullable: false),
                    DonorEmailProtected = table.Column<string>(type: "text", nullable: false),
                    DonorPhoneProtected = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "donation_events",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DonationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_donation_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_donation_events_donations_DonationId",
                        column: x => x.DonationId,
                        principalSchema: "finance",
                        principalTable: "donations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ActorId_Id",
                schema: "finance",
                table: "audit_events",
                columns: new[] { "ActorId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_At",
                schema: "finance",
                table: "audit_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Entity_EntityId_Id",
                schema: "finance",
                table: "audit_events",
                columns: new[] { "Entity", "EntityId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_RequestId",
                schema: "finance",
                table: "audit_events",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_donation_events_DonationId_Id",
                schema: "finance",
                table: "donation_events",
                columns: new[] { "DonationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_donations_DesignationType_DesignationId",
                schema: "finance",
                table: "donations",
                columns: new[] { "DesignationType", "DesignationId" });

            migrationBuilder.CreateIndex(
                name: "IX_donations_ReceivedOn",
                schema: "finance",
                table: "donations",
                column: "ReceivedOn");

            migrationBuilder.CreateIndex(
                name: "IX_donations_RecurringPlanId",
                schema: "finance",
                table: "donations",
                column: "RecurringPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_donations_Reference",
                schema: "finance",
                table: "donations",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_donations_ReversesDonationId",
                schema: "finance",
                table: "donations",
                column: "ReversesDonationId",
                unique: true,
                filter: "\"ReversesDonationId\" IS NOT NULL");

            // The ledger is append-only in the database too (PostgreSQL only): no UPDATE or DELETE, whatever statement or tool issues it.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE FUNCTION finance.refuse_ledger_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
                    BEGIN
                        RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP USING ERRCODE = '42501';
                    END;
                    $fn$;
                    CREATE TRIGGER donations_append_only BEFORE UPDATE OR DELETE ON finance.donations
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER donation_events_append_only BEFORE UPDATE OR DELETE ON finance.donation_events
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER recurring_plans_append_only BEFORE UPDATE OR DELETE ON finance.recurring_plans
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER audit_events_append_only BEFORE UPDATE OR DELETE ON finance.audit_events
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER donations_append_only ON finance.donations; DROP TRIGGER donation_events_append_only ON finance.donation_events; DROP TRIGGER recurring_plans_append_only ON finance.recurring_plans; DROP TRIGGER audit_events_append_only ON finance.audit_events; DROP FUNCTION finance.refuse_ledger_change();");
            }

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "donation_events",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "recurring_plans",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "donations",
                schema: "finance");
        }
    }
}
