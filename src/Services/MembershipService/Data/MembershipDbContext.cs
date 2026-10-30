using KJWebsite.BuildingBlocks.Auditing;
using MembershipService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Data;

public sealed class MembershipDbContext(DbContextOptions<MembershipDbContext> options) : DbContext(options)
{
    public DbSet<ApplicationEntity> Applications => Set<ApplicationEntity>();
    public DbSet<AttachmentEntity> Attachments => Set<AttachmentEntity>();
    public DbSet<CampaignEntity> Campaigns => Set<CampaignEntity>();
    public DbSet<ApplicationStatusEventEntity> StatusEvents => Set<ApplicationStatusEventEntity>();
    public DbSet<ApplicationNoteEntity> Notes => Set<ApplicationNoteEntity>();
    public DbSet<MemberEntity> Members => Set<MemberEntity>();
    public DbSet<NotificationOutboxEntity> NotificationOutbox => Set<NotificationOutboxEntity>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseHistoryEdits();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseHistoryEdits();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // A past transition is never rewritten. (Deleting a whole application, for retention or erasure, removes its history with it.)
    private void RefuseHistoryEdits()
    {
        if (ChangeTracker.Entries<ApplicationStatusEventEntity>().Any(e => e.State == EntityState.Modified))
        {
            throw new InvalidOperationException("Application status history is append-only.");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("membership");
        modelBuilder.ConfigureAudit();

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
            entity.Property(x => x.CampaignId).HasMaxLength(64);
            entity.Property(x => x.ReviewerId).HasMaxLength(64);
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
            entity.HasIndex(x => x.CampaignId);
            entity.HasIndex(x => x.Status);
            entity.HasMany(x => x.Notes).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.StatusEvents).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<ApplicationStatusEventEntity>(entity =>
        {
            entity.ToTable("application_status_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ApplicationId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.By).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.ApplicationId);
        });

        modelBuilder.Entity<ApplicationNoteEntity>(entity =>
        {
            entity.ToTable("application_notes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.ApplicationId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.By).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Text).HasMaxLength(4000).IsRequired();
            entity.HasIndex(x => x.ApplicationId);
        });

        modelBuilder.Entity<NotificationOutboxEntity>(entity =>
        {
            entity.ToTable("notification_outbox");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ApplicationId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Template).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(8).IsRequired();
            entity.Property(x => x.RecipientProtected).IsRequired();
            entity.HasIndex(x => x.SentAt);
        });

        modelBuilder.Entity<MemberEntity>(entity =>
        {
            entity.ToTable("members");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.MemberCode).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ApplicationId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.LastName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => x.MemberCode).IsUnique();
            entity.HasIndex(x => x.UserId).IsUnique();
            entity.HasIndex(x => x.ApplicationId).IsUnique();
        });

        modelBuilder.Entity<CampaignEntity>(entity =>
        {
            entity.ToTable("campaigns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            entity.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NameBn).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => x.Slug).IsUnique();
        });
    }
}
