using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore;
using ReportingService.Data.Entities;

namespace ReportingService.Data;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    public DbSet<AggregatePointEntity> AggregatePoints => Set<AggregatePointEntity>();
    public DbSet<AggregateSourceEntity> AggregateSources => Set<AggregateSourceEntity>();
    public DbSet<AggregateRunEntity> AggregateRuns => Set<AggregateRunEntity>();
    public DbSet<MetricDefinitionEntity> MetricDefinitions => Set<MetricDefinitionEntity>();

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

        modelBuilder.Entity<MetricDefinitionEntity>(e =>
        {
            e.ToTable("metric_definitions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Key).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            e.Property(x => x.Methodology).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(32).IsRequired();
            e.Property(x => x.Metric).HasMaxLength(80).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Scale).HasPrecision(18, 8);
            e.Property(x => x.CreatedBy).HasMaxLength(64);
            e.HasIndex(x => new { x.Key, x.Version }).IsUnique();
        });
    }
}
