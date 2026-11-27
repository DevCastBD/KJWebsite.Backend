using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentService.Migrations
{
    /// <inheritdoc />
    public partial class EventAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckInSource",
                schema: "content",
                table: "event_registrations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckedInAt",
                schema: "content",
                table: "event_registrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "event_certificates",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RegistrationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EventItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NameProtected = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IssuedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_certificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_event_certificates_event_registrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalSchema: "content",
                        principalTable: "event_registrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_feedback_forms",
                schema: "content",
                columns: table => new
                {
                    EventItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    QuestionsJson = table.Column<string>(type: "text", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_feedback_forms", x => x.EventItemId);
                    table.ForeignKey(
                        name: "FK_event_feedback_forms_content_items_EventItemId",
                        column: x => x.EventItemId,
                        principalSchema: "content",
                        principalTable: "content_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_feedback_responses",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EventItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RegistrationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AnswersJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_feedback_responses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_event_feedback_responses_event_registrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalSchema: "content",
                        principalTable: "event_registrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_event_certificates_Number",
                schema: "content",
                table: "event_certificates",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_certificates_RegistrationId",
                schema: "content",
                table: "event_certificates",
                column: "RegistrationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_feedback_responses_EventItemId",
                schema: "content",
                table: "event_feedback_responses",
                column: "EventItemId");

            migrationBuilder.CreateIndex(
                name: "IX_event_feedback_responses_RegistrationId",
                schema: "content",
                table: "event_feedback_responses",
                column: "RegistrationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_certificates",
                schema: "content");

            migrationBuilder.DropTable(
                name: "event_feedback_forms",
                schema: "content");

            migrationBuilder.DropTable(
                name: "event_feedback_responses",
                schema: "content");

            migrationBuilder.DropColumn(
                name: "CheckInSource",
                schema: "content",
                table: "event_registrations");

            migrationBuilder.DropColumn(
                name: "CheckedInAt",
                schema: "content",
                table: "event_registrations");
        }
    }
}
