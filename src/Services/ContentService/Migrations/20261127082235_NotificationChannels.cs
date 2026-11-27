using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ContentService.Migrations
{
    /// <inheritdoc />
    public partial class NotificationChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "in_app_notifications",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Subject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_in_app_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                schema: "content",
                columns: table => new
                {
                    Subject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Email = table.Column<bool>(type: "boolean", nullable: false),
                    Sms = table.Column<bool>(type: "boolean", nullable: false),
                    InApp = table.Column<bool>(type: "boolean", nullable: false),
                    Push = table.Column<bool>(type: "boolean", nullable: false),
                    Phone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Lang = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => x.Subject);
                });

            migrationBuilder.CreateTable(
                name: "push_subscriptions",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    P256dh = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Auth = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ConsentedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_subscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sms_messages",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Subject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ToPhone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Lang = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Encoding = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Segments = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedUnix = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sms_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_in_app_notifications_Subject",
                schema: "content",
                table: "in_app_notifications",
                column: "Subject");

            migrationBuilder.CreateIndex(
                name: "IX_push_subscriptions_Endpoint",
                schema: "content",
                table: "push_subscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_push_subscriptions_Subject",
                schema: "content",
                table: "push_subscriptions",
                column: "Subject");

            migrationBuilder.CreateIndex(
                name: "IX_sms_messages_CreatedUnix",
                schema: "content",
                table: "sms_messages",
                column: "CreatedUnix");

            migrationBuilder.CreateIndex(
                name: "IX_sms_messages_Status",
                schema: "content",
                table: "sms_messages",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "in_app_notifications",
                schema: "content");

            migrationBuilder.DropTable(
                name: "notification_preferences",
                schema: "content");

            migrationBuilder.DropTable(
                name: "push_subscriptions",
                schema: "content");

            migrationBuilder.DropTable(
                name: "sms_messages",
                schema: "content");
        }
    }
}
