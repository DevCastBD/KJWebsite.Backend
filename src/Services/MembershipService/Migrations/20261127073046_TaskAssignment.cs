using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class TaskAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "member_notifications",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Template = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AssigneeMemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectSlug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CommitteeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Priority = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReminderForDueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    OverdueNoticeForDueOn = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tasks_members_AssigneeMemberId",
                        column: x => x.AssigneeMemberId,
                        principalSchema: "membership",
                        principalTable: "members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_member_notifications_MemberId_ReadAt",
                schema: "membership",
                table: "member_notifications",
                columns: new[] { "MemberId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_AssigneeMemberId_Status",
                schema: "membership",
                table: "tasks",
                columns: new[] { "AssigneeMemberId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_CommitteeId",
                schema: "membership",
                table: "tasks",
                column: "CommitteeId");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_ProjectSlug",
                schema: "membership",
                table: "tasks",
                column: "ProjectSlug");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_notifications",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "tasks",
                schema: "membership");
        }
    }
}
