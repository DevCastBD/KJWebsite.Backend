using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class DataQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "member_duplicate_decisions",
                schema: "membership",
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
                    table.PrimaryKey("PK_member_duplicate_decisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_member_duplicate_decisions_PairKey",
                schema: "membership",
                table: "member_duplicate_decisions",
                column: "PairKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_duplicate_decisions",
                schema: "membership");
        }
    }
}
