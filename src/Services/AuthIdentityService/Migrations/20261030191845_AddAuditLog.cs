using System;
using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AuthIdentityService.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "auth",
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

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ActorId_Id",
                schema: "auth",
                table: "audit_events",
                columns: new[] { "ActorId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_At",
                schema: "auth",
                table: "audit_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Entity_EntityId_Id",
                schema: "auth",
                table: "audit_events",
                columns: new[] { "Entity", "EntityId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_RequestId",
                schema: "auth",
                table: "audit_events",
                column: "RequestId");

            // The log refuses UPDATE/DELETE/TRUNCATE in the database itself (PostgreSQL only).
            migrationBuilder.AddImmutability("auth");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveImmutability("auth");

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "auth");
        }
    }
}
