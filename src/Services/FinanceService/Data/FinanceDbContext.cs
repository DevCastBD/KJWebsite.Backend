using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Data;

public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : DbContext(options)
{
    public DbSet<DonationEntity> Donations => Set<DonationEntity>();
    public DbSet<DonationEventEntity> DonationEvents => Set<DonationEventEntity>();
    public DbSet<RecurringPlanEntity> RecurringPlans => Set<RecurringPlanEntity>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseLedgerEdits();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseLedgerEdits();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Financial records are append-only: a correction is a new reversing entry. PostgreSQL enforces the same with triggers (see the migration),
    // so a bulk ExecuteUpdate or a hand-written statement is refused too.
    private void RefuseLedgerEdits()
    {
        ChangeTracker.DetectChanges();
        var offender = ChangeTracker.Entries().FirstOrDefault(e =>
            e.Entity is DonationEntity or DonationEventEntity or RecurringPlanEntity && e.State is EntityState.Modified or EntityState.Deleted);
        if (offender is not null)
        {
            throw new InvalidOperationException($"{offender.Metadata.ClrType.Name} records are append-only: post a reversing entry instead of changing or deleting one.");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("finance");
        modelBuilder.ConfigureAudit();

        modelBuilder.Entity<DonationEntity>(entity =>
        {
            entity.ToTable("donations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.Reference).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.FeeAmount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.DesignationType).HasMaxLength(16).IsRequired();
            entity.Property(x => x.DesignationId).HasMaxLength(100);
            entity.Property(x => x.Dedication).HasMaxLength(500);
            entity.Property(x => x.DonorNameProtected).IsRequired();
            entity.Property(x => x.DonorEmailProtected).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(2).IsRequired();
            entity.Property(x => x.RecurringPlanId).HasMaxLength(40);
            entity.Property(x => x.ReversesDonationId).HasMaxLength(40);
            entity.Property(x => x.ReversalReason).HasMaxLength(500);
            entity.Property(x => x.CreatedBy).HasMaxLength(64);
            entity.HasIndex(x => x.Reference).IsUnique();
            // One reversal per donation: a second would refund it twice.
            entity.HasIndex(x => x.ReversesDonationId).IsUnique().HasFilter("\"ReversesDonationId\" IS NOT NULL");
            entity.HasIndex(x => new { x.DesignationType, x.DesignationId });
            entity.HasIndex(x => x.ReceivedOn);
            entity.HasIndex(x => x.RecurringPlanId);
            entity.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.DonationId).OnDelete(DeleteBehavior.Restrict);
            // Sqlite (test-only) stores decimals as text, so a numeric comparison in SQL would be wrong there; PostgreSQL enforces the sign rule.
            if (Database.IsNpgsql())
            {
                entity.ToTable(t => t.HasCheckConstraint("ck_donations_kind_sign",
                    "(\"Kind\" = 'reversal' AND \"Amount\" < 0 AND \"FeeAmount\" <= 0) OR (\"Kind\" <> 'reversal' AND \"Amount\" > 0 AND \"FeeAmount\" >= 0)"));
            }
        });

        modelBuilder.Entity<DonationEventEntity>(entity =>
        {
            entity.ToTable("donation_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.DonationId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Note).HasMaxLength(500);
            entity.HasIndex(x => new { x.DonationId, x.Id });
        });

        modelBuilder.Entity<RecurringPlanEntity>(entity =>
        {
            entity.ToTable("recurring_plans");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.Interval).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.FeeAmount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.DesignationType).HasMaxLength(16).IsRequired();
            entity.Property(x => x.DesignationId).HasMaxLength(100);
            entity.Property(x => x.Dedication).HasMaxLength(500);
            entity.Property(x => x.DonorNameProtected).IsRequired();
            entity.Property(x => x.DonorEmailProtected).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(2).IsRequired();
        });
    }
}
