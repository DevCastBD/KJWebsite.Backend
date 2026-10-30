using MembershipService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Data;

public sealed class MembershipDbContext(DbContextOptions<MembershipDbContext> options) : DbContext(options)
{
    public DbSet<ApplicationEntity> Applications => Set<ApplicationEntity>();
    public DbSet<AttachmentEntity> Attachments => Set<AttachmentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("membership");

        // ICU collation is a PostgreSQL feature; Sqlite (test-only provider) has no "bn-x-icu" collation to register.
        var useBengaliCollation = Database.IsNpgsql();
        if (useBengaliCollation)
        {
            modelBuilder.HasCollation("bn-x-icu", locale: "bn", provider: "icu");
        }

        modelBuilder.Entity<ApplicationEntity>(entity =>
        {
            entity.ToTable("applications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Reference).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(8).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.LastName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Gender).HasMaxLength(32);
            entity.Property(x => x.AcademicInstitute).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Occupation).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ReasonForJoining).HasMaxLength(2000);
            entity.Property(x => x.PresentOrganization).HasMaxLength(200);
            entity.Property(x => x.VolunteeringExperience).HasMaxLength(2000);
            entity.Property(x => x.HeardAboutUs).HasMaxLength(200);
            entity.Property(x => x.CityOfResidence).HasMaxLength(100);
            entity.Property(x => x.CountryOfResidence).HasMaxLength(100);
            entity.Property(x => x.EmailProtected).IsRequired();
            entity.Property(x => x.PhoneProtected).IsRequired();
            entity.Property(x => x.BloodGroupProtected).IsRequired();
            entity.Property(x => x.CurrentAddressProtected).IsRequired();
            entity.Property(x => x.EmailHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PhoneHash).HasMaxLength(64).IsRequired();
            if (useBengaliCollation)
            {
                entity.Property(x => x.FirstName).UseCollation("bn-x-icu");
                entity.Property(x => x.LastName).UseCollation("bn-x-icu");
            }

            entity.HasIndex(x => x.Reference).IsUnique();
            entity.HasIndex(x => x.EmailHash);
            entity.HasIndex(x => x.PhoneHash);
            entity.HasIndex(x => x.CreatedAt);
            entity.HasMany(x => x.Attachments).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AttachmentEntity>(entity =>
        {
            entity.ToTable("application_attachments");
            entity.HasKey(x => new { x.ApplicationId, x.Kind });
            entity.Property(x => x.ApplicationId).HasMaxLength(64);
            entity.Property(x => x.Kind).HasMaxLength(16);
            entity.Property(x => x.StorageKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ScanStatus).HasMaxLength(16).IsRequired();
        });
    }
}
