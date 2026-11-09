using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthIdentityService.Migrations
{
    /// <inheritdoc />
    public partial class DonorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                schema: "auth",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // One-time backfill (#209 D9): roles that already exist keep what an administrator gave them, so the four new sponsorship keys
            // reach an existing Admin role only here. Admin never receives sponsorship.link.void (SuperAdmin only). The migration history
            // records that this ran, so a later removal of a key through the role API is never undone. A fresh database gets the same keys
            // from KjRoles.Seed when the Admin role is created.
            migrationBuilder.Sql(@"
                INSERT INTO auth.role_claims (""RoleId"", ""ClaimType"", ""ClaimValue"") SELECT ""Id"", 'permissions', 'finance.donor.provision' FROM auth.roles WHERE ""NormalizedName"" = 'ADMIN' AND NOT EXISTS (SELECT 1 FROM auth.role_claims c WHERE c.""RoleId"" = auth.roles.""Id"" AND c.""ClaimType"" = 'permissions' AND c.""ClaimValue"" = 'finance.donor.provision');
                INSERT INTO auth.role_claims (""RoleId"", ""ClaimType"", ""ClaimValue"") SELECT ""Id"", 'permissions', 'sponsorship.view' FROM auth.roles WHERE ""NormalizedName"" = 'ADMIN' AND NOT EXISTS (SELECT 1 FROM auth.role_claims c WHERE c.""RoleId"" = auth.roles.""Id"" AND c.""ClaimType"" = 'permissions' AND c.""ClaimValue"" = 'sponsorship.view');
                INSERT INTO auth.role_claims (""RoleId"", ""ClaimType"", ""ClaimValue"") SELECT ""Id"", 'permissions', 'sponsorship.student.manage' FROM auth.roles WHERE ""NormalizedName"" = 'ADMIN' AND NOT EXISTS (SELECT 1 FROM auth.role_claims c WHERE c.""RoleId"" = auth.roles.""Id"" AND c.""ClaimType"" = 'permissions' AND c.""ClaimValue"" = 'sponsorship.student.manage');
                INSERT INTO auth.role_claims (""RoleId"", ""ClaimType"", ""ClaimValue"") SELECT ""Id"", 'permissions', 'sponsorship.link.manage' FROM auth.roles WHERE ""NormalizedName"" = 'ADMIN' AND NOT EXISTS (SELECT 1 FROM auth.role_claims c WHERE c.""RoleId"" = auth.roles.""Id"" AND c.""ClaimType"" = 'permissions' AND c.""ClaimValue"" = 'sponsorship.link.manage');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM auth.role_claims WHERE ""ClaimType"" = 'permissions' AND ""ClaimValue"" IN ('finance.donor.provision', 'sponsorship.view', 'sponsorship.student.manage', 'sponsorship.link.manage') AND ""RoleId"" IN (SELECT ""Id"" FROM auth.roles WHERE ""NormalizedName"" = 'ADMIN');
            ");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                schema: "auth",
                table: "users");
        }
    }
}
