using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentService.Migrations
{
    /// <inheritdoc />
    public partial class InitialContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "content");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:CollationDefinition:bn-x-icu", "bn,bn,icu,");

            migrationBuilder.CreateTable(
                name: "content_items",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "content_translations",
                schema: "content",
                columns: table => new
                {
                    ContentItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Lang = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false, collation: "bn-x-icu"),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    AttributesJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_translations", x => new { x.ContentItemId, x.Lang });
                    table.ForeignKey(
                        name: "FK_content_translations_content_items_ContentItemId",
                        column: x => x.ContentItemId,
                        principalSchema: "content",
                        principalTable: "content_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "news_articles",
                schema: "content",
                columns: table => new
                {
                    ContentItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_news_articles", x => x.ContentItemId);
                    table.ForeignKey(
                        name: "FK_news_articles_content_items_ContentItemId",
                        column: x => x.ContentItemId,
                        principalSchema: "content",
                        principalTable: "content_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                schema: "content",
                columns: table => new
                {
                    ContentItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: false),
                    Icon = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.ContentItemId);
                    table.ForeignKey(
                        name: "FK_projects_content_items_ContentItemId",
                        column: x => x.ContentItemId,
                        principalSchema: "content",
                        principalTable: "content_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_content_items_Type_Slug",
                schema: "content",
                table: "content_items",
                columns: new[] { "Type", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_content_items_Type_SortOrder",
                schema: "content",
                table: "content_items",
                columns: new[] { "Type", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_translations",
                schema: "content");

            migrationBuilder.DropTable(
                name: "news_articles",
                schema: "content");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "content");

            migrationBuilder.DropTable(
                name: "content_items",
                schema: "content");
        }
    }
}
