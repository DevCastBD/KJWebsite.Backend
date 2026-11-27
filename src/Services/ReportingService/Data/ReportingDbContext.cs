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
    public DbSet<ReportDefinitionEntity> ReportDefinitions => Set<ReportDefinitionEntity>();
    public DbSet<ReportRunEntity> ReportRuns => Set<ReportRunEntity>();
    public DbSet<ReportFileEntity> ReportFiles => Set<ReportFileEntity>();
    public DbSet<ReportScheduleEntity> ReportSchedules => Set<ReportScheduleEntity>();
    public DbSet<ReportRecipientEntity> ReportRecipients => Set<ReportRecipientEntity>();
    public DbSet<ReportDeliveryEntity> ReportDeliveries => Set<ReportDeliveryEntity>();
    public DbSet<AnalyticsEventEntity> AnalyticsEvents => Set<AnalyticsEventEntity>();

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

        modelBuilder.Entity<ReportDefinitionEntity>(e =>
        {
            e.ToTable("report_definitions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Template).HasMaxLength(40).IsRequired();
            e.Property(x => x.ParametersJson).HasMaxLength(2000).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(64);
        });

        modelBuilder.Entity<ReportRunEntity>(e =>
        {
            e.ToTable("report_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.ReportId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ScheduleId).HasMaxLength(64);
            e.Property(x => x.Trigger).HasMaxLength(16).IsRequired();
            e.Property(x => x.ParametersJson).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.GeneratedBy).HasMaxLength(64);
            e.HasIndex(x => x.ReportId);
        });

        modelBuilder.Entity<ReportFileEntity>(e =>
        {
            e.ToTable("report_files");
            e.HasKey(x => x.Id);
            e.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Format).HasMaxLength(8).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.RunId, x.Format }).IsUnique();
        });

        modelBuilder.Entity<ReportScheduleEntity>(e =>
        {
            e.ToTable("report_schedules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.ReportId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Formats).HasMaxLength(32).IsRequired();
            e.Property(x => x.ParametersJson).HasMaxLength(2000).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(64);
            e.HasIndex(x => new { x.Active, x.NextRunAt });
        });

        modelBuilder.Entity<ReportRecipientEntity>(e =>
        {
            e.ToTable("report_recipients");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.ScheduleId).HasMaxLength(64).IsRequired();
            e.Property(x => x.EmailProtected).IsRequired();
            e.Property(x => x.EmailKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.AddedBy).HasMaxLength(64);
            e.Property(x => x.RemovedBy).HasMaxLength(64);
            e.HasIndex(x => new { x.ScheduleId, x.EmailKey });
        });

        modelBuilder.Entity<ReportDeliveryEntity>(e =>
        {
            e.ToTable("report_deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            e.Property(x => x.RecipientId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => x.RunId);
        });

        modelBuilder.Entity<AnalyticsEventEntity>(e =>
        {
            e.ToTable("analytics_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(40).IsRequired();
            e.Property(x => x.Step).HasMaxLength(40);
            e.Property(x => x.Field).HasMaxLength(40);
            e.Property(x => x.Run).HasMaxLength(40);
            e.Property(x => x.Path).HasMaxLength(200);
            e.Property(x => x.Visitor).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.Name, x.At });
        });
    }
}
