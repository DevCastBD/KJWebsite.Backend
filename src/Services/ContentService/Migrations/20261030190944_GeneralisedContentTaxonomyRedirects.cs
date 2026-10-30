using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentService.Migrations
{
    /// <inheritdoc />
    public partial class GeneralisedContentTaxonomyRedirects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                schema: "content",
                table: "content_items",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "content_relations",
                schema: "content",
                columns: table => new
                {
                    FromItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ToItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_relations", x => new { x.FromItemId, x.ToItemId });
                    table.ForeignKey(
                        name: "FK_content_relations_content_items_FromItemId",
                        column: x => x.FromItemId,
                        principalSchema: "content",
                        principalTable: "content_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_content_relations_content_items_ToItemId",
                        column: x => x.ToItemId,
                        principalSchema: "content",
                        principalTable: "content_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "redirects",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FromPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ToPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_redirects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                schema: "content",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "content_item_tags",
                schema: "content",
                columns: table => new
                {
                    ContentItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TagId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_item_tags", x => new { x.ContentItemId, x.TagId });
                    table.ForeignKey(
                        name: "FK_content_item_tags_content_items_ContentItemId",
                        column: x => x.ContentItemId,
                        principalSchema: "content",
                        principalTable: "content_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_content_item_tags_tags_TagId",
                        column: x => x.TagId,
                        principalSchema: "content",
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tag_names",
                schema: "content",
                columns: table => new
                {
                    TagId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Lang = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag_names", x => new { x.TagId, x.Lang });
                    table.ForeignKey(
                        name: "FK_tag_names_tags_TagId",
                        column: x => x.TagId,
                        principalSchema: "content",
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_content_item_tags_TagId",
                schema: "content",
                table: "content_item_tags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_content_relations_ToItemId",
                schema: "content",
                table: "content_relations",
                column: "ToItemId");

            migrationBuilder.CreateIndex(
                name: "IX_redirects_FromPath",
                schema: "content",
                table: "redirects",
                column: "FromPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_redirects_ToPath",
                schema: "content",
                table: "redirects",
                column: "ToPath");

            migrationBuilder.CreateIndex(
                name: "IX_tag_names_Lang_NormalizedName",
                schema: "content",
                table: "tag_names",
                columns: new[] { "Lang", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tags_Slug",
                schema: "content",
                table: "tags",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_item_tags",
                schema: "content");

            migrationBuilder.DropTable(
                name: "content_relations",
                schema: "content");

            migrationBuilder.DropTable(
                name: "redirects",
                schema: "content");

            migrationBuilder.DropTable(
                name: "tag_names",
                schema: "content");

            migrationBuilder.DropTable(
                name: "tags",
                schema: "content");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                schema: "content",
                table: "content_items");
        }
    }
}
