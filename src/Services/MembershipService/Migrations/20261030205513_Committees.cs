using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class Committees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "committee_members",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CommitteeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PositionKey = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    PositionEn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PositionBn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_committee_members", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "committees",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameBn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameBnIsFallback = table.Column<bool>(type: "boolean", nullable: false),
                    ParentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_committees", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_committee_members_CommitteeId",
                schema: "membership",
                table: "committee_members",
                column: "CommitteeId");

            migrationBuilder.CreateIndex(
                name: "IX_committee_members_MemberId",
                schema: "membership",
                table: "committee_members",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_committees_ParentId",
                schema: "membership",
                table: "committees",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_committees_Slug",
                schema: "membership",
                table: "committees",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "committee_members",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "committees",
                schema: "membership");
        }
    }
}
