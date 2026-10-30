using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MembershipService.Migrations
{
    /// <inheritdoc />
    public partial class MemberLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConsentAlumni",
                schema: "membership",
                table: "members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "member_status_events",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReasonProtected = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_status_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_member_status_events_MemberId",
                schema: "membership",
                table: "member_status_events",
                column: "MemberId");

            // Members approved before the lifecycle existed start their service on the day they joined.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    INSERT INTO membership.member_status_events ("MemberId", "FromStatus", "ToStatus", "EffectiveOn", "At", "ActorId")
                    SELECT m."Id", NULL, 'active', (m."JoinedAt" AT TIME ZONE 'UTC')::date, m."JoinedAt", 'system'
                    FROM membership.members m
                    WHERE NOT EXISTS (SELECT 1 FROM membership.member_status_events e WHERE e."MemberId" = m."Id");
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_status_events",
                schema: "membership");

            migrationBuilder.DropColumn(
                name: "ConsentAlumni",
                schema: "membership",
                table: "members");
        }
    }
}
