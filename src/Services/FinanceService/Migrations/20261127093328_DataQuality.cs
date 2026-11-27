using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class DataQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "donation_anomalies",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Rule = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DonationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReviewedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_donation_anomalies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "donor_duplicate_decisions",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PairKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Decision = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_donor_duplicate_decisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_donation_anomalies_DedupeKey",
                schema: "finance",
                table: "donation_anomalies",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_donation_anomalies_Status_DetectedAt",
                schema: "finance",
                table: "donation_anomalies",
                columns: new[] { "Status", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_donor_duplicate_decisions_PairKey",
                schema: "finance",
                table: "donor_duplicate_decisions",
                column: "PairKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "donation_anomalies",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "donor_duplicate_decisions",
                schema: "finance");
        }
    }
}
