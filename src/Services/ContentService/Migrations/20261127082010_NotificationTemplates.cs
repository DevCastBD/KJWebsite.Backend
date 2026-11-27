using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ContentService.Migrations
{
    /// <inheritdoc />
    public partial class NotificationTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_sends",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Lang = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ToEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    BodyHtml = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_sends", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_templates",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Lang = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_templates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_sends_Key",
                schema: "content",
                table: "notification_sends",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_notification_templates_Key_Lang_Version",
                schema: "content",
                table: "notification_templates",
                columns: new[] { "Key", "Lang", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_sends",
                schema: "content");

            migrationBuilder.DropTable(
                name: "notification_templates",
                schema: "content");
        }
    }
}
