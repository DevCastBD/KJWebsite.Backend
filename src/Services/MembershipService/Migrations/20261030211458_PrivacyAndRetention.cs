using System;
using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "MemberId",
                schema: "membership",
                table: "deletion_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<string>(
                name: "ApplicationId",
                schema: "membership",
                table: "deletion_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                schema: "membership",
                table: "deletion_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletedBy",
                schema: "membership",
                table: "deletion_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "consent_records",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Granted = table.Column<bool>(type: "boolean", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NoticeVersion = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "erasure_records",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExecutedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SummaryJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erasure_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "retention_items",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApplicationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StatusSince = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "retention_runs",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    StartedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyJson = table.Column<string>(type: "text", nullable: false),
                    Purged = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_runs", x => x.Id);
                });

            // The member consent log becomes the consent ledger: same rows, with the purpose spelled out and a source.
            migrationBuilder.Sql("""
                INSERT INTO membership.consent_records ("SubjectId", "SubjectKind", "Purpose", "Granted", "At", "Source", "ActorId")
                SELECT "MemberId", 'member', 'publish_' || "Field", "Granted", "At", 'portal', 'system' FROM membership.member_consent_events;
                """);
            migrationBuilder.DropTable(
                name: "member_consent_events",
                schema: "membership");

            migrationBuilder.CreateIndex(
                name: "IX_deletion_requests_ApplicationId",
                schema: "membership",
                table: "deletion_requests",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_SubjectId",
                schema: "membership",
                table: "consent_records",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_retention_items_RunId",
                schema: "membership",
                table: "retention_items",
                column: "RunId");

            // Erasure may null the actor's email, IP and browser on their own audit rows, and nothing else.
            migrationBuilder.AllowActorRedaction("membership");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consent_records",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "erasure_records",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "retention_items",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "retention_runs",
                schema: "membership");

            migrationBuilder.DropIndex(
                name: "IX_deletion_requests_ApplicationId",
                schema: "membership",
                table: "deletion_requests");

            migrationBuilder.DropColumn(
                name: "ApplicationId",
                schema: "membership",
                table: "deletion_requests");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                schema: "membership",
                table: "deletion_requests");

            migrationBuilder.DropColumn(
                name: "CompletedBy",
                schema: "membership",
                table: "deletion_requests");

            migrationBuilder.AlterColumn<string>(
                name: "MemberId",
                schema: "membership",
                table: "deletion_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "member_consent_events",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Field = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Granted = table.Column<bool>(type: "boolean", nullable: false),
                    MemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_consent_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_member_consent_events_MemberId",
                schema: "membership",
                table: "member_consent_events",
                column: "MemberId");
        }
    }
}
