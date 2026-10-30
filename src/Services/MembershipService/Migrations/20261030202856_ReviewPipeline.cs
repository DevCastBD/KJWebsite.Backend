using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class ReviewPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InterviewAt",
                schema: "membership",
                table: "applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewerId",
                schema: "membership",
                table: "applications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Score",
                schema: "membership",
                table: "applications",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "By",
                schema: "membership",
                table: "application_status_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Notified",
                schema: "membership",
                table: "application_status_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "application_notes",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApplicationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    By = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_application_notes_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "membership",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_outbox",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApplicationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Template = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    RecipientProtected = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_outbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_applications_Status",
                schema: "membership",
                table: "applications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_application_notes_ApplicationId",
                schema: "membership",
                table: "application_notes",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_outbox_SentAt",
                schema: "membership",
                table: "notification_outbox",
                column: "SentAt");

            // The first status was called "received" before the review pipeline; it is "awaiting" now.
            migrationBuilder.Sql("UPDATE membership.applications SET \"Status\" = 'awaiting' WHERE \"Status\" = 'received';");
            migrationBuilder.Sql("UPDATE membership.application_status_events SET \"Status\" = 'awaiting' WHERE \"Status\" = 'received';");

            // History is append-only in the database too (PostgreSQL only); deleting an application still removes its rows.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    CREATE FUNCTION membership.status_events_refuse_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                    BEGIN
                        RAISE EXCEPTION 'application_status_events is append-only: UPDATE is not allowed' USING ERRCODE = '42501';
                    END;
                    $fn$;
                    CREATE TRIGGER status_events_no_update BEFORE UPDATE ON membership.application_status_events
                        FOR EACH ROW EXECUTE FUNCTION membership.status_events_refuse_update();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER status_events_no_update ON membership.application_status_events; DROP FUNCTION membership.status_events_refuse_update();");
            }

            migrationBuilder.DropTable(
                name: "application_notes",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "notification_outbox",
                schema: "membership");

            migrationBuilder.DropIndex(
                name: "IX_applications_Status",
                schema: "membership",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "InterviewAt",
                schema: "membership",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "ReviewerId",
                schema: "membership",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "Score",
                schema: "membership",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "By",
                schema: "membership",
                table: "application_status_events");

            migrationBuilder.DropColumn(
                name: "Notified",
                schema: "membership",
                table: "application_status_events");
        }
    }
}
