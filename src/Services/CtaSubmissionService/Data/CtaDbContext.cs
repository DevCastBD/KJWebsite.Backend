using CtaSubmissionService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CtaSubmissionService.Data;

public sealed class CtaDbContext(DbContextOptions<CtaDbContext> options) : DbContext(options)
{
    public DbSet<CtaSubmissionEntity> Submissions => Set<CtaSubmissionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("cta");

        // ICU collation is a PostgreSQL feature; Sqlite (test-only provider) has no "bn-x-icu" collation to register.
        var useBengaliCollation = Database.IsNpgsql();
        if (useBengaliCollation)
        {
            modelBuilder.HasCollation("bn-x-icu", locale: "bn", provider: "icu");
        }

        modelBuilder.Entity<CtaSubmissionEntity>(entity =>
        {
            entity.ToTable("cta_submissions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.FormName).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CtaType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(8).IsRequired();
            entity.Property(x => x.SourcePath).HasMaxLength(512).IsRequired();
            entity.Property(x => x.ValuesJson).IsRequired();
            if (useBengaliCollation)
            {
                entity.Property(x => x.FirstName).UseCollation("bn-x-icu");
                entity.Property(x => x.LastName).UseCollation("bn-x-icu");
            }
            entity.HasIndex(x => x.CreatedAt);
            entity.Property(x => x.Category).HasMaxLength(32).IsRequired().HasDefaultValue("general");
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired().HasDefaultValue("new");
            entity.Property(x => x.Priority).HasMaxLength(16).IsRequired().HasDefaultValue("normal");
            entity.Property(x => x.AssignedTo).HasMaxLength(64);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.AssignedTo);
        });

        modelBuilder.Entity<EngagementRuleEntity>(entity =>
        {
            entity.ToTable("engagement_rules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.MatchCategory).HasMaxLength(32);
            entity.Property(x => x.MatchLanguage).HasMaxLength(8);
            entity.Property(x => x.MatchKeyword).HasMaxLength(120);
            entity.Property(x => x.AssignTo).HasMaxLength(64);
            entity.Property(x => x.Priority).HasMaxLength(16);
            entity.Property(x => x.SetCategory).HasMaxLength(32);
            entity.HasIndex(x => x.Position);
        });

        modelBuilder.Entity<EngagementSlaTargetEntity>(entity =>
        {
            entity.ToTable("engagement_sla_targets");
            entity.HasKey(x => x.Category);
            entity.Property(x => x.Category).HasMaxLength(32);
        });

        modelBuilder.Entity<EngagementEventEntity>(entity =>
        {
            entity.ToTable("engagement_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SubmissionId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Actor).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Detail).HasMaxLength(1000);
            entity.HasIndex(x => x.SubmissionId);
        });
    }
}
