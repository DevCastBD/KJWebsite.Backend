using System;
using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SponsorshipService.Migrations
{
    /// <inheritdoc />
    public partial class InitialSponsorship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sponsorship");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "sponsorship",
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
                name: "students",
                schema: "sponsorship",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NameProtected = table.Column<string>(type: "text", nullable: false),
                    PreferredName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DateOfBirthProtected = table.Column<string>(type: "text", nullable: false),
                    Gender = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    School = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClassName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProgrammeId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    District = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AddressProtected = table.Column<string>(type: "text", nullable: true),
                    GuardianNameProtected = table.Column<string>(type: "text", nullable: false),
                    GuardianPhoneProtected = table.Column<string>(type: "text", nullable: true),
                    StoryEn = table.Column<string>(type: "text", nullable: true),
                    StoryBn = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PhotoKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PhotoContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FormKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FormContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FormFileName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_students", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "consents",
                schema: "sponsorship",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Granted = table.Column<bool>(type: "boolean", nullable: false),
                    FormKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_consents_students_StudentId",
                        column: x => x.StudentId,
                        principalSchema: "sponsorship",
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ActorId_Id",
                schema: "sponsorship",
                table: "audit_events",
                columns: new[] { "ActorId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_At",
                schema: "sponsorship",
                table: "audit_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Entity_EntityId_Id",
                schema: "sponsorship",
                table: "audit_events",
                columns: new[] { "Entity", "EntityId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_RequestId",
                schema: "sponsorship",
                table: "audit_events",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_consents_StudentId_Id",
                schema: "sponsorship",
                table: "consents",
                columns: new[] { "StudentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_students_ProgrammeId",
                schema: "sponsorship",
                table: "students",
                column: "ProgrammeId");

            migrationBuilder.CreateIndex(
                name: "IX_students_Status",
                schema: "sponsorship",
                table: "students",
                column: "Status");

            migrationBuilder.AddImmutability("sponsorship");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "sponsorship");

            migrationBuilder.DropTable(
                name: "consents",
                schema: "sponsorship");

            migrationBuilder.DropTable(
                name: "students",
                schema: "sponsorship");
        }
    }
}
