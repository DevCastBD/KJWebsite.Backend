using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore;

namespace SponsorshipService.Data;

public sealed class SponsorshipDbContext(DbContextOptions<SponsorshipDbContext> options) : DbContext(options)
{
    public DbSet<StudentEntity> Students => Set<StudentEntity>();
    public DbSet<LinkEntity> Links => Set<LinkEntity>();
    public DbSet<FrozenCopyEntity> FrozenCopies => Set<FrozenCopyEntity>();
    public DbSet<ConsentEntity> Consents => Set<ConsentEntity>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseConsentEdits();
        RefuseFrozenCopyEdits();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseConsentEdits();
        RefuseFrozenCopyEdits();
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

    private void RefuseFrozenCopyEdits()
    {
        if (ChangeTracker.Entries<FrozenCopyEntity>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Frozen copies are append-only.");
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

        modelBuilder.Entity<LinkEntity>(entity =>
        {
            entity.ToTable("links");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.DonorId).HasMaxLength(40);
            entity.Property(x => x.DonorSubject).HasMaxLength(64);
            entity.Property(x => x.StudentId).HasMaxLength(64);
            entity.Property(x => x.EndType).HasMaxLength(16);
            entity.Property(x => x.EndReason).HasMaxLength(500);
            entity.Property(x => x.CreatedBy).HasMaxLength(64);
            entity.Property(x => x.EndedBy).HasMaxLength(64);
            entity.Property(x => x.NoticeStatus).HasMaxLength(16);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<StudentEntity>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            // Every donor read looks a donor's links up by DonorId, ended ones included, which the filtered unique indexes below cannot serve.
            entity.HasIndex(x => x.DonorId).HasDatabaseName("IX_links_DonorId");
            entity.HasIndex(x => x.StudentId).IsUnique().HasDatabaseName("UX_links_active_student").HasFilter("\"EndType\" IS NULL");
            entity.HasIndex(x => new { x.DonorId, x.StudentId }).IsUnique().HasDatabaseName("UX_links_active_pair").HasFilter("\"EndType\" IS NULL");
        });
        modelBuilder.Entity<FrozenCopyEntity>(entity =>
        {
            entity.ToTable("frozen_copies");
            entity.HasKey(x => x.LinkId);
            entity.Property(x => x.LinkId).HasMaxLength(40);
            entity.Property(x => x.StudentId).HasMaxLength(64);
            entity.Property(x => x.PreferredName).HasMaxLength(100);
            entity.Property(x => x.Class).HasMaxLength(50);
            entity.Property(x => x.School).HasMaxLength(200);
            entity.Property(x => x.ProgrammeId).HasMaxLength(200);
            entity.Property(x => x.District).HasMaxLength(100);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.Property(x => x.PhotoKey).HasMaxLength(64);
            entity.Property(x => x.PhotoContentType).HasMaxLength(32);
            entity.HasIndex(x => x.PhotoKey);
            entity.HasOne<LinkEntity>().WithOne().HasForeignKey<FrozenCopyEntity>(x => x.LinkId).OnDelete(DeleteBehavior.Restrict);
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
