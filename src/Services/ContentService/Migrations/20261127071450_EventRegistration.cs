using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ContentService.Migrations
{
    /// <inheritdoc />
    public partial class EventRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                schema: "content",
                table: "events",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "PriceMinor",
                schema: "content",
                table: "events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RegistrationOpen",
                schema: "content",
                table: "events",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "RegistrationVersion",
                schema: "content",
                table: "events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresApproval",
                schema: "content",
                table: "events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "event_notifications",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegistrationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Template = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    RecipientProtected = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "event_registrations",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EventItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurrenceStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TicketCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NameProtected = table.Column<string>(type: "text", nullable: false),
                    EmailProtected = table.Column<string>(type: "text", nullable: false),
                    PhoneProtected = table.Column<string>(type: "text", nullable: true),
                    EmailKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    DeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PaymentReference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_registrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_event_registrations_content_items_EventItemId",
                        column: x => x.EventItemId,
                        principalSchema: "content",
                        principalTable: "content_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_event_notifications_SentAt",
                schema: "content",
                table: "event_notifications",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_event_registrations_EventItemId_EmailKey",
                schema: "content",
                table: "event_registrations",
                columns: new[] { "EventItemId", "EmailKey" });

            migrationBuilder.CreateIndex(
                name: "IX_event_registrations_EventItemId_OccurrenceStartUtc_Status",
                schema: "content",
                table: "event_registrations",
                columns: new[] { "EventItemId", "OccurrenceStartUtc", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_event_registrations_TicketCode",
                schema: "content",
                table: "event_registrations",
                column: "TicketCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_notifications",
                schema: "content");

            migrationBuilder.DropTable(
                name: "event_registrations",
                schema: "content");

            migrationBuilder.DropColumn(
                name: "Currency",
                schema: "content",
                table: "events");

            migrationBuilder.DropColumn(
                name: "PriceMinor",
                schema: "content",
                table: "events");

            migrationBuilder.DropColumn(
                name: "RegistrationOpen",
                schema: "content",
                table: "events");

            migrationBuilder.DropColumn(
                name: "RegistrationVersion",
                schema: "content",
                table: "events");

            migrationBuilder.DropColumn(
                name: "RequiresApproval",
                schema: "content",
                table: "events");
        }
    }
}
