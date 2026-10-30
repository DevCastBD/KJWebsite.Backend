using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthIdentityService.Migrations
{
    /// <inheritdoc />
    public partial class HashedRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows hold plain-text tokens. They are discarded rather than hashed: every old session ends.
            migrationBuilder.Sql("DELETE FROM auth.refresh_tokens;");

            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_Token",
                schema: "auth",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "Token",
                schema: "auth",
                table: "refresh_tokens");

            migrationBuilder.AddColumn<string>(
                name: "FamilyId",
                schema: "auth",
                table: "refresh_tokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReplacedById",
                schema: "auth",
                table: "refresh_tokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SessionCreatedAt",
                schema: "auth",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                schema: "auth",
                table: "refresh_tokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                schema: "auth",
                table: "refresh_tokens",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_FamilyId",
                schema: "auth",
                table: "refresh_tokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                schema: "auth",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_FamilyId",
                schema: "auth",
                table: "refresh_tokens");

            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_TokenHash",
                schema: "auth",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                schema: "auth",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "ReplacedById",
                schema: "auth",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "SessionCreatedAt",
                schema: "auth",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                schema: "auth",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                schema: "auth",
                table: "refresh_tokens");

            migrationBuilder.AddColumn<string>(
                name: "Token",
                schema: "auth",
                table: "refresh_tokens",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_Token",
                schema: "auth",
                table: "refresh_tokens",
                column: "Token",
                unique: true);
        }
    }
}
