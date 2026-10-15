using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CtaSubmissionService.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cta");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:CollationDefinition:bn-x-icu", "bn,bn,icu,");

            migrationBuilder.CreateTable(
                name: "cta_submissions",
                schema: "cta",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FormName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CtaType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ValuesJson = table.Column<string>(type: "text", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FirstName = table.Column<string>(type: "text", nullable: true, collation: "bn-x-icu"),
                    LastName = table.Column<string>(type: "text", nullable: true, collation: "bn-x-icu"),
                    Gender = table.Column<string>(type: "text", nullable: true),
                    ReasonForJoining = table.Column<string>(type: "text", nullable: true),
                    PresentOrganization = table.Column<string>(type: "text", nullable: true),
                    VolunteeingExperience = table.Column<string>(type: "text", nullable: true),
                    DateOfBirth = table.Column<string>(type: "text", nullable: true),
                    CityOfResidence = table.Column<string>(type: "text", nullable: true),
                    CountryOfResidence = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cta_submissions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cta_submissions_CreatedAt",
                schema: "cta",
                table: "cta_submissions",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cta_submissions",
                schema: "cta");
        }
    }
}
