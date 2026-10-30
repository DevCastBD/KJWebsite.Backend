using System.Security.Claims;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthIdentityService.Identity;

public static partial class AuthSeeder
{
    public const string DevAdminEmail = "admin@site.org";
    private const string DevAdminPassword = "admin123";

    public static async Task SeedAsync(IServiceProvider services, bool isDevelopment)
    {
        var db = services.GetRequiredService<AuthDbContext>();
        var roles = services.GetRequiredService<RoleManager<AuthRoleEntity>>();
        var users = services.GetRequiredService<UserManager<AuthUserEntity>>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AuthSeeder));

        await SeedRolesAsync(roles);

        var admin = await users.FindByEmailAsync(DevAdminEmail);
        if (!isDevelopment)
        {
            // A database seeded in Development carries a published password. Refuse it everywhere else.
            if (admin is not null && admin.Status != "disabled")
            {
                admin.Status = "disabled";
                admin.LockoutEnabled = true;
                admin.LockoutEnd = DateTimeOffset.MaxValue;
                await users.UpdateAsync(admin);
                LogDevAdminDisabled(logger);
            }

            return;
        }

        if (admin is not null)
        {
            // A dev database migrated from the plain-text schema keeps the account but not its password.
            if (admin.PasswordHash is null)
            {
                admin.PasswordHash = users.PasswordHasher.HashPassword(admin, DevAdminPassword);
                admin.EmailConfirmed = true;
                admin.LockoutEnabled = true;
                await users.UpdateAsync(admin);
            }

            if (!await users.IsInRoleAsync(admin, KjRoles.SuperAdmin))
            {
                await users.AddToRoleAsync(admin, KjRoles.SuperAdmin);
            }

            return;
        }

        // The dev password is deliberately shorter than the production policy, so it is hashed directly
        // rather than created through UserManager's validators. It exists only in Development.
        admin = new AuthUserEntity
        {
            Id = "usr_admin",
            UserName = DevAdminEmail,
            Email = DevAdminEmail,
            EmailConfirmed = true,
            LockoutEnabled = true,
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            SecurityStamp = Guid.NewGuid().ToString("N")
        };
        admin.NormalizedUserName = users.NormalizeName(admin.UserName);
        admin.NormalizedEmail = users.NormalizeEmail(admin.Email);
        admin.PasswordHash = users.PasswordHasher.HashPassword(admin, DevAdminPassword);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        await users.AddToRoleAsync(admin, KjRoles.SuperAdmin);
    }

    /// <summary>
    /// Adds any role or permission from the catalog that is missing. The migration seeds the same rows on PostgreSQL;
    /// this keeps SQLite (tests) and later catalog additions in step. It never removes a claim someone granted.
    /// </summary>
    public static async Task SeedRolesAsync(RoleManager<AuthRoleEntity> roles)
    {
        foreach (var (name, permissions) in KjRoles.Seed)
        {
            var role = await roles.FindByNameAsync(name);
            if (role is null)
            {
                await roles.CreateAsync(new AuthRoleEntity(name));
                role = await roles.FindByNameAsync(name);
            }

            var held = (await roles.GetClaimsAsync(role!)).Where(c => c.Type == KjClaims.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var permission in permissions.Where(p => !held.Contains(p)))
            {
                await roles.AddClaimAsync(role!, new Claim(KjClaims.Permission, permission));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The development seed account exists outside Development and has been disabled.")]
    private static partial void LogDevAdminDisabled(ILogger logger);
}

internal static class TimingDummyHash
{
    public static readonly string Value = new PasswordHasher<AuthUserEntity>().HashPassword(new AuthUserEntity(), Guid.NewGuid().ToString("N"));
}
