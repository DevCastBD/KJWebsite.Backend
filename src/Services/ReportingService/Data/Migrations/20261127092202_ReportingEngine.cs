using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ReportingService.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportingEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "report_definitions",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Template = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ParametersJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "report_deliveries",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecipientId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_deliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "report_files",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Format = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "report_recipients",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ScheduleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EmailProtected = table.Column<string>(type: "text", nullable: false),
                    EmailKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AddedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RemovedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_recipients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "report_runs",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReportId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ScheduleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ParametersJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Rows = table.Column<int>(type: "integer", nullable: false),
                    GeneratedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "report_schedules",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReportId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    HourUtc = table.Column<int>(type: "integer", nullable: false),
                    Formats = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ParametersJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    FailingSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_schedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_deliveries_RunId",
                schema: "reporting",
                table: "report_deliveries",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_report_files_RunId_Format",
                schema: "reporting",
                table: "report_files",
                columns: new[] { "RunId", "Format" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_recipients_ScheduleId_EmailKey",
                schema: "reporting",
                table: "report_recipients",
                columns: new[] { "ScheduleId", "EmailKey" });

            migrationBuilder.CreateIndex(
                name: "IX_report_runs_ReportId",
                schema: "reporting",
                table: "report_runs",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_report_schedules_Active_NextRunAt",
                schema: "reporting",
                table: "report_schedules",
                columns: new[] { "Active", "NextRunAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_definitions",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "report_deliveries",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "report_files",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "report_recipients",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "report_runs",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "report_schedules",
                schema: "reporting");
        }
    }
}
