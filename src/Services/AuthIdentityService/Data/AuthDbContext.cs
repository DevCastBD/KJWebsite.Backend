using AuthIdentityService.Data.Entities;
using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AuthIdentityService.Data;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<AuthUserEntity, AuthRoleEntity, string>(options)
{
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<MemberProfileEntity> MemberProfiles => Set<MemberProfileEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("auth");
        builder.ConfigureAudit();

        builder.Entity<AuthUserEntity>(entity =>
        {
            entity.ToTable("users");
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ProvisionedFor).HasMaxLength(64);
            entity.HasIndex(x => x.ProvisionedFor).IsUnique();
            entity.HasOne(x => x.Profile).WithOne().HasForeignKey<MemberProfileEntity>(p => p.UserId);
        });

        builder.Entity<AuthRoleEntity>().ToTable("roles");
        builder.Entity<IdentityUserRole<string>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<string>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<string>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<string>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<string>>().ToTable("role_claims");

        builder.Entity<MemberProfileEntity>(entity =>
        {
            entity.ToTable("member_profiles");
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.UserId).HasMaxLength(64);
        });

        builder.Entity<RefreshTokenEntity>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FamilyId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ReplacedById).HasMaxLength(64);
            entity.Property(x => x.UserAgent).HasMaxLength(256);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.FamilyId);
            entity.HasIndex(x => x.UserId);
        });
    }
}
