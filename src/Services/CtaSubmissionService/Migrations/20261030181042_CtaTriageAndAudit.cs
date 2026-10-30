using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CtaSubmissionService.Migrations
{
    /// <inheritdoc />
    public partial class CtaTriageAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssigneeId",
                schema: "cta",
                table: "cta_submissions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                schema: "cta",
                table: "cta_submissions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "cta",
                table: "cta_submissions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "new");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                schema: "cta",
                table: "cta_submissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "submission_audit",
                schema: "cta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_submission_audit", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cta_submissions_Status",
                schema: "cta",
                table: "cta_submissions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_submission_audit_At",
                schema: "cta",
                table: "submission_audit",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_submission_audit_SubmissionId",
                schema: "cta",
                table: "submission_audit",
                column: "SubmissionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "submission_audit",
                schema: "cta");

            migrationBuilder.DropIndex(
                name: "IX_cta_submissions_Status",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropColumn(
                name: "AssigneeId",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropColumn(
                name: "Notes",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "cta",
                table: "cta_submissions");
        }
    }
}
