using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ReportingService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnalyticsEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analytics_events",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Step = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Field = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Run = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Path = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Visitor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_At",
                schema: "reporting",
                table: "analytics_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_Name_At",
                schema: "reporting",
                table: "analytics_events",
                columns: new[] { "Name", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analytics_events",
                schema: "reporting");
        }
    }
}
