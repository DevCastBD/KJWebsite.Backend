using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "membership");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:CollationDefinition:bn-x-icu", "bn,bn,icu,");

            migrationBuilder.CreateTable(
                name: "applications",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "bn-x-icu"),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "bn-x-icu"),
                    Gender = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    AcademicInstitute = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Occupation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReasonForJoining = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PresentOrganization = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VolunteeringExperience = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    HeardAboutUs = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CityOfResidence = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CountryOfResidence = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EmailProtected = table.Column<string>(type: "text", nullable: false),
                    PhoneProtected = table.Column<string>(type: "text", nullable: false),
                    BloodGroupProtected = table.Column<string>(type: "text", nullable: false),
                    CurrentAddressProtected = table.Column<string>(type: "text", nullable: false),
                    DateOfBirthProtected = table.Column<string>(type: "text", nullable: true),
                    EmailHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PhoneHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "application_attachments",
                schema: "membership",
                columns: table => new
                {
                    ApplicationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    ScanStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_attachments", x => new { x.ApplicationId, x.Kind });
                    table.ForeignKey(
                        name: "FK_application_attachments_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "membership",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_applications_CreatedAt",
                schema: "membership",
                table: "applications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_applications_EmailHash",
                schema: "membership",
                table: "applications",
                column: "EmailHash");

            migrationBuilder.CreateIndex(
                name: "IX_applications_PhoneHash",
                schema: "membership",
                table: "applications",
                column: "PhoneHash");

            migrationBuilder.CreateIndex(
                name: "IX_applications_Reference",
                schema: "membership",
                table: "applications",
                column: "Reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_attachments",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "applications",
                schema: "membership");
        }
    }
}
