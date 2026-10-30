using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthIdentityService.Migrations
{
    /// <inheritdoc />
    public partial class AuditActorRedaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Erasure may null the actor's email, IP and browser on their own audit rows, and nothing else.
            migrationBuilder.AllowActorRedaction("auth");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
