using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentService.Migrations
{
    /// <inheritdoc />
    public partial class NewsletterCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BodyHtml",
                schema: "content",
                table: "newsletter_messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CampaignId",
                schema: "content",
                table: "newsletter_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Error",
                schema: "content",
                table: "newsletter_messages",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NotBeforeUnix",
                schema: "content",
                table: "newsletter_messages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SentAt",
                schema: "content",
                table: "newsletter_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "newsletter_campaigns",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SegmentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SubjectBn = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    BodyBn = table.Column<string>(type: "text", nullable: false),
                    SubjectEn = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    BodyEn = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RecipientCount = table.Column<int>(type: "integer", nullable: false),
                    SendStartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_newsletter_campaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "newsletter_templates",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SubjectBn = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    BodyBn = table.Column<string>(type: "text", nullable: false),
                    SubjectEn = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    BodyEn = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_newsletter_templates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_newsletter_messages_CampaignId",
                schema: "content",
                table: "newsletter_messages",
                column: "CampaignId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "newsletter_campaigns",
                schema: "content");

            migrationBuilder.DropTable(
                name: "newsletter_templates",
                schema: "content");

            migrationBuilder.DropIndex(
                name: "IX_newsletter_messages_CampaignId",
                schema: "content",
                table: "newsletter_messages");

            migrationBuilder.DropColumn(
                name: "BodyHtml",
                schema: "content",
                table: "newsletter_messages");

            migrationBuilder.DropColumn(
                name: "CampaignId",
                schema: "content",
                table: "newsletter_messages");

            migrationBuilder.DropColumn(
                name: "Error",
                schema: "content",
                table: "newsletter_messages");

            migrationBuilder.DropColumn(
                name: "NotBeforeUnix",
                schema: "content",
                table: "newsletter_messages");

            migrationBuilder.DropColumn(
                name: "SentAt",
                schema: "content",
                table: "newsletter_messages");
        }
    }
}
