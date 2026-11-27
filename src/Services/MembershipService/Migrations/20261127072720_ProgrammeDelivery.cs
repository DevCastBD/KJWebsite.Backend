using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class ProgrammeDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "beneficiaries",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameBn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AddressProtected = table.Column<string>(type: "text", nullable: true),
                    District = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_beneficiaries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "programme_outcomes",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectSlug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    MetricKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<double>(type: "double precision", nullable: false),
                    Unit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_programme_outcomes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mous",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BeneficiaryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SignedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    SignatoryProtected = table.Column<string>(type: "text", nullable: false),
                    SignatoryRole = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DocumentKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DocumentContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DocumentFileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DocumentBytes = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mous", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mous_beneficiaries_BeneficiaryId",
                        column: x => x.BeneficiaryId,
                        principalSchema: "membership",
                        principalTable: "beneficiaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "programme_enrolments",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectSlug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BeneficiaryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Students = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_programme_enrolments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_programme_enrolments_beneficiaries_BeneficiaryId",
                        column: x => x.BeneficiaryId,
                        principalSchema: "membership",
                        principalTable: "beneficiaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_logs",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectSlug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BeneficiaryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HeldOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Attendance = table.Column<int>(type: "integer", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    LoggedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LoggedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_logs_beneficiaries_BeneficiaryId",
                        column: x => x.BeneficiaryId,
                        principalSchema: "membership",
                        principalTable: "beneficiaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_beneficiaries_Slug",
                schema: "membership",
                table: "beneficiaries",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mous_BeneficiaryId",
                schema: "membership",
                table: "mous",
                column: "BeneficiaryId");

            migrationBuilder.CreateIndex(
                name: "IX_programme_enrolments_BeneficiaryId",
                schema: "membership",
                table: "programme_enrolments",
                column: "BeneficiaryId");

            migrationBuilder.CreateIndex(
                name: "IX_programme_enrolments_ProjectSlug_BeneficiaryId",
                schema: "membership",
                table: "programme_enrolments",
                columns: new[] { "ProjectSlug", "BeneficiaryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_programme_outcomes_ProjectSlug_MetricKey_PeriodStart_Period~",
                schema: "membership",
                table: "programme_outcomes",
                columns: new[] { "ProjectSlug", "MetricKey", "PeriodStart", "PeriodEnd" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_logs_BeneficiaryId",
                schema: "membership",
                table: "session_logs",
                column: "BeneficiaryId");

            migrationBuilder.CreateIndex(
                name: "IX_session_logs_ClientId",
                schema: "membership",
                table: "session_logs",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_logs_ProjectSlug_HeldOn",
                schema: "membership",
                table: "session_logs",
                columns: new[] { "ProjectSlug", "HeldOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mous",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "programme_enrolments",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "programme_outcomes",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "session_logs",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "beneficiaries",
                schema: "membership");
        }
    }
}
