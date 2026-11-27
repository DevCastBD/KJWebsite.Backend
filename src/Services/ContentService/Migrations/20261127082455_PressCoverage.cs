using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentService.Migrations
{
    /// <inheritdoc />
    public partial class PressCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "press_coverage",
                schema: "content",
                columns: table => new
                {
                    ContentItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outlet = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OutletLogoUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    PublishedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ExternalUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ClippingUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ReproductionPermission = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PermissionNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Featured = table.Column<bool>(type: "boolean", nullable: false),
                    FeaturedOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_press_coverage", x => x.ContentItemId);
                    table.ForeignKey(
                        name: "FK_press_coverage_content_items_ContentItemId",
                        column: x => x.ContentItemId,
                        principalSchema: "content",
                        principalTable: "content_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_press_coverage_Featured_FeaturedOrder",
                schema: "content",
                table: "press_coverage",
                columns: new[] { "Featured", "FeaturedOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "press_coverage",
                schema: "content");
        }
    }
}
