using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CtaSubmissionService.Migrations
{
    /// <inheritdoc />
    public partial class EngagementInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedTo",
                schema: "cta",
                table: "cta_submissions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                schema: "cta",
                table: "cta_submissions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "general");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                schema: "cta",
                table: "cta_submissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstResponseAt",
                schema: "cta",
                table: "cta_submissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                schema: "cta",
                table: "cta_submissions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "normal");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "cta",
                table: "cta_submissions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "new");

            migrationBuilder.CreateTable(
                name: "engagement_events",
                schema: "cta",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubmissionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engagement_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "engagement_rules",
                schema: "cta",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    MatchCategory = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MatchLanguage = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    MatchKeyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    AssignTo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SetCategory = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engagement_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "engagement_sla_targets",
                schema: "cta",
                columns: table => new
                {
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResponseMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engagement_sla_targets", x => x.Category);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cta_submissions_AssignedTo",
                schema: "cta",
                table: "cta_submissions",
                column: "AssignedTo");

            migrationBuilder.CreateIndex(
                name: "IX_cta_submissions_Status",
                schema: "cta",
                table: "cta_submissions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_engagement_events_SubmissionId",
                schema: "cta",
                table: "engagement_events",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_engagement_rules_Position",
                schema: "cta",
                table: "engagement_rules",
                column: "Position");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "engagement_events",
                schema: "cta");

            migrationBuilder.DropTable(
                name: "engagement_rules",
                schema: "cta");

            migrationBuilder.DropTable(
                name: "engagement_sla_targets",
                schema: "cta");

            migrationBuilder.DropIndex(
                name: "IX_cta_submissions_AssignedTo",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropIndex(
                name: "IX_cta_submissions_Status",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropColumn(
                name: "AssignedTo",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropColumn(
                name: "Category",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropColumn(
                name: "FirstResponseAt",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "cta",
                table: "cta_submissions");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "cta",
                table: "cta_submissions");
        }
    }
}
