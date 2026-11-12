using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore;

namespace SponsorshipService.Data;

public sealed class SponsorshipDbContext(DbContextOptions<SponsorshipDbContext> options) : DbContext(options)
{
    public DbSet<StudentEntity> Students => Set<StudentEntity>();
    public DbSet<ConsentEntity> Consents => Set<ConsentEntity>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseConsentEdits();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseConsentEdits();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // A consent row is evidence: it is never changed or removed, only followed by a later row.
    private void RefuseConsentEdits()
    {
        if (ChangeTracker.Entries<ConsentEntity>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Consent records are append-only.");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("sponsorship");
        modelBuilder.ConfigureAudit();

        modelBuilder.Entity<StudentEntity>(entity =>
        {
            entity.ToTable("students");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.NameProtected).IsRequired();
            entity.Property(x => x.PreferredName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.DateOfBirthProtected).IsRequired();
            entity.Property(x => x.Gender).HasMaxLength(16);
            entity.Property(x => x.School).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ClassName).HasMaxLength(50).IsRequired();
            entity.Property(x => x.ProgrammeId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.District).HasMaxLength(100).IsRequired();
            entity.Property(x => x.GuardianNameProtected).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.PhotoKey).HasMaxLength(64);
            entity.Property(x => x.PhotoContentType).HasMaxLength(32);
            entity.Property(x => x.FormKey).HasMaxLength(64);
            entity.Property(x => x.FormContentType).HasMaxLength(32);
            entity.Property(x => x.FormFileName).HasMaxLength(120);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.ProgrammeId);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<ConsentEntity>(entity =>
        {
            entity.ToTable("consents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.StudentId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FormKey).HasMaxLength(64);
            entity.Property(x => x.ActorId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Note).HasMaxLength(500);
            entity.HasIndex(x => new { x.StudentId, x.Id });
            entity.HasOne<StudentEntity>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
