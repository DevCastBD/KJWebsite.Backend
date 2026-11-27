using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class VolunteerOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "volunteer_hours",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectSlug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EventSlug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Minutes = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volunteer_hours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volunteer_hours_members_MemberId",
                        column: x => x.MemberId,
                        principalSchema: "membership",
                        principalTable: "members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "volunteer_opportunities",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TitleEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TitleBn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DescriptionEn = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DescriptionBn = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ProjectSlug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Skills = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volunteer_opportunities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "volunteer_shifts",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OpportunityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Slots = table.Column<int>(type: "integer", nullable: false),
                    SignedUp = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volunteer_shifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volunteer_shifts_volunteer_opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalSchema: "membership",
                        principalTable: "volunteer_opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "volunteer_signups",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShiftId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volunteer_signups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volunteer_signups_members_MemberId",
                        column: x => x.MemberId,
                        principalSchema: "membership",
                        principalTable: "members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_volunteer_signups_volunteer_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalSchema: "membership",
                        principalTable: "volunteer_shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_hours_MemberId_Status",
                schema: "membership",
                table: "volunteer_hours",
                columns: new[] { "MemberId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_hours_ProjectSlug_Status",
                schema: "membership",
                table: "volunteer_hours",
                columns: new[] { "ProjectSlug", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_opportunities_Slug",
                schema: "membership",
                table: "volunteer_opportunities",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_shifts_OpportunityId",
                schema: "membership",
                table: "volunteer_shifts",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_signups_MemberId",
                schema: "membership",
                table: "volunteer_signups",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_volunteer_signups_ShiftId_MemberId",
                schema: "membership",
                table: "volunteer_signups",
                columns: new[] { "ShiftId", "MemberId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "volunteer_hours",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "volunteer_signups",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "volunteer_shifts",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "volunteer_opportunities",
                schema: "membership");
        }
    }
}
