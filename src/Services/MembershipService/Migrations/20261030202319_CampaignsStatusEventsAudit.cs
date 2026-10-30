using System;
using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class CampaignsStatusEventsAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CampaignId",
                schema: "membership",
                table: "applications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "application_status_events",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApplicationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_status_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_application_status_events_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "membership",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Service = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Entity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Before = table.Column<string>(type: "text", nullable: true),
                    After = table.Column<string>(type: "text", nullable: true),
                    Operation = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RequestId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "campaigns",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameBn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OpensAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosesAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_applications_CampaignId",
                schema: "membership",
                table: "applications",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_application_status_events_ApplicationId",
                schema: "membership",
                table: "application_status_events",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ActorId_Id",
                schema: "membership",
                table: "audit_events",
                columns: new[] { "ActorId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_At",
                schema: "membership",
                table: "audit_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Entity_EntityId_Id",
                schema: "membership",
                table: "audit_events",
                columns: new[] { "Entity", "EntityId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_RequestId",
                schema: "membership",
                table: "audit_events",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_Slug",
                schema: "membership",
                table: "campaigns",
                column: "Slug",
                unique: true);

            // The log refuses UPDATE/DELETE/TRUNCATE in the database itself (PostgreSQL only).
            migrationBuilder.AddImmutability("membership");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_status_events",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "campaigns",
                schema: "membership");

            migrationBuilder.DropIndex(
                name: "IX_applications_CampaignId",
                schema: "membership",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "CampaignId",
                schema: "membership",
                table: "applications");
        }
    }
}
