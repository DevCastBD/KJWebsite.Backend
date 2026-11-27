using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore;
using ReportingService.Data.Entities;

namespace ReportingService.Data;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    public DbSet<AggregatePointEntity> AggregatePoints => Set<AggregatePointEntity>();
    public DbSet<AggregateSourceEntity> AggregateSources => Set<AggregateSourceEntity>();
    public DbSet<AggregateRunEntity> AggregateRuns => Set<AggregateRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("reporting");
        modelBuilder.ConfigureAudit();

        modelBuilder.Entity<AggregatePointEntity>(e =>
        {
            e.ToTable("aggregate_points");
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).HasMaxLength(32).IsRequired();
            e.Property(x => x.Metric).HasMaxLength(80).IsRequired();
            e.Property(x => x.Dimension).HasMaxLength(120).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
            e.Property(x => x.Value).HasPrecision(20, 4);
            e.HasIndex(x => new { x.Source, x.Metric, x.Period, x.Dimension, x.Currency }).IsUnique();
            e.HasIndex(x => new { x.Metric, x.Period });
        });

        modelBuilder.Entity<AggregateSourceEntity>(e =>
        {
            e.ToTable("aggregate_sources");
            e.HasKey(x => x.Source);
            e.Property(x => x.Source).HasMaxLength(32);
            e.Property(x => x.RunId).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<AggregateRunEntity>(e =>
        {
            e.ToTable("aggregate_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Trigger).HasMaxLength(16).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.Error).HasMaxLength(2000);
        });
    }
}
