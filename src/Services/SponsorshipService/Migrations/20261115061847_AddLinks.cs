using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SponsorshipService.Migrations
{
    /// <inheritdoc />
    public partial class AddLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "links",
                schema: "sponsorship",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DonorId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DonorSubject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StudentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NoticeStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_links_students_StudentId",
                        column: x => x.StudentId,
                        principalSchema: "sponsorship",
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "frozen_copies",
                schema: "sponsorship",
                columns: table => new
                {
                    LinkId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StudentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreferredName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Class = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    School = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProgrammeId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProgrammeNameEn = table.Column<string>(type: "text", nullable: false),
                    ProgrammeNameBn = table.Column<string>(type: "text", nullable: false),
                    District = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StoryEn = table.Column<string>(type: "text", nullable: true),
                    StoryBn = table.Column<string>(type: "text", nullable: true),
                    PhotoKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PhotoContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FrozenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_frozen_copies", x => x.LinkId);
                    table.ForeignKey(
                        name: "FK_frozen_copies_links_LinkId",
                        column: x => x.LinkId,
                        principalSchema: "sponsorship",
                        principalTable: "links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_frozen_copies_PhotoKey",
                schema: "sponsorship",
                table: "frozen_copies",
                column: "PhotoKey");

            migrationBuilder.CreateIndex(
                name: "IX_links_DonorId",
                schema: "sponsorship",
                table: "links",
                column: "DonorId");

            migrationBuilder.CreateIndex(
                name: "UX_links_active_pair",
                schema: "sponsorship",
                table: "links",
                columns: new[] { "DonorId", "StudentId" },
                unique: true,
                filter: "\"EndType\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_links_active_student",
                schema: "sponsorship",
                table: "links",
                column: "StudentId",
                unique: true,
                filter: "\"EndType\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "frozen_copies",
                schema: "sponsorship");

            migrationBuilder.DropTable(
                name: "links",
                schema: "sponsorship");
        }
    }
}
