using CtaSubmissionService.Data.Entities;
using KJWebsite.BuildingBlocks.Outbox;
using Microsoft.EntityFrameworkCore;

namespace CtaSubmissionService.Data;

public sealed class CtaDbContext(DbContextOptions<CtaDbContext> options) : DbContext(options), IOutboxDbContext
{
    public DbSet<CtaSubmissionEntity> Submissions => Set<CtaSubmissionEntity>();

    public DbSet<SubmissionAuditEntity> SubmissionAudit => Set<SubmissionAuditEntity>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<DeadLetterMessage> DeadLetters => Set<DeadLetterMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("cta");
        modelBuilder.ConfigureOutbox();

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
            entity.Property(x => x.Status).HasMaxLength(32).HasDefaultValue(SubmissionStatuses.New).IsRequired();
            entity.Property(x => x.AssigneeId).HasMaxLength(64);
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<SubmissionAuditEntity>(entity =>
        {
            entity.ToTable("submission_audit");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SubmissionId).HasMaxLength(64);
            entity.Property(x => x.Actor).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Detail).HasMaxLength(2000);
            entity.HasIndex(x => x.SubmissionId);
            entity.HasIndex(x => x.At);
        });

        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            // Sqlite (test-only provider) cannot compare or order DateTimeOffset; store it as a sortable integer there.
            var converter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter();
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties().Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
                {
                    property.SetValueConverter(converter);
                }
            }
        }
    }
}
