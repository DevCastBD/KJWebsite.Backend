using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class MemberSelfService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressProtected",
                schema: "membership",
                table: "members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BloodGroupProtected",
                schema: "membership",
                table: "members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConsentExpertise",
                schema: "membership",
                table: "members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ConsentName",
                schema: "membership",
                table: "members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ConsentPhotograph",
                schema: "membership",
                table: "members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DateOfBirthProtected",
                schema: "membership",
                table: "members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Expertise",
                schema: "membership",
                table: "members",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameBn",
                schema: "membership",
                table: "members",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyEvents",
                schema: "membership",
                table: "members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyMembership",
                schema: "membership",
                table: "members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyNews",
                schema: "membership",
                table: "members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PhoneProtected",
                schema: "membership",
                table: "members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PhotoBytes",
                schema: "membership",
                table: "members",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoContentType",
                schema: "membership",
                table: "members",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoFileName",
                schema: "membership",
                table: "members",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoKey",
                schema: "membership",
                table: "members",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoScanStatus",
                schema: "membership",
                table: "members",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "deletion_requests",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "member_consent_events",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Field = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Granted = table.Column<bool>(type: "boolean", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_consent_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "profile_changes",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Field = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValueProtected = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_changes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deletion_requests_MemberId",
                schema: "membership",
                table: "deletion_requests",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_member_consent_events_MemberId",
                schema: "membership",
                table: "member_consent_events",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_profile_changes_MemberId_Field",
                schema: "membership",
                table: "profile_changes",
                columns: new[] { "MemberId", "Field" });

            migrationBuilder.CreateIndex(
                name: "IX_profile_changes_Status",
                schema: "membership",
                table: "profile_changes",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deletion_requests",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "member_consent_events",
                schema: "membership");

            migrationBuilder.DropTable(
                name: "profile_changes",
                schema: "membership");

            migrationBuilder.DropColumn(
                name: "AddressProtected",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "BloodGroupProtected",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "ConsentExpertise",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "ConsentName",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "ConsentPhotograph",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "DateOfBirthProtected",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "Expertise",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "NameBn",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "NotifyEvents",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "NotifyMembership",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "NotifyNews",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "PhoneProtected",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "PhotoBytes",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "PhotoContentType",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "PhotoFileName",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "PhotoKey",
                schema: "membership",
                table: "members");

            migrationBuilder.DropColumn(
                name: "PhotoScanStatus",
                schema: "membership",
                table: "members");
        }
    }
}
