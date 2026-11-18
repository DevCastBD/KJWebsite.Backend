using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringPlanDonorId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DonorId",
                schema: "finance",
                table: "recurring_plans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_recurring_plans_DonorId",
                schema: "finance",
                table: "recurring_plans",
                column: "DonorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_recurring_plans_DonorId",
                schema: "finance",
                table: "recurring_plans");

            migrationBuilder.DropColumn(
                name: "DonorId",
                schema: "finance",
                table: "recurring_plans");
        }
    }
}
