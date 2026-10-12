using CtaSubmissionService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CtaSubmissionService.Data;

public sealed class CtaDbContext(DbContextOptions<CtaDbContext> options) : DbContext(options)
{
    public DbSet<CtaSubmissionEntity> Submissions => Set<CtaSubmissionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("cta");
        modelBuilder.HasCollation("bn-x-icu", locale: "bn", provider: "icu");

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
            entity.Property(x => x.FirstName).UseCollation("bn-x-icu");
            entity.Property(x => x.LastName).UseCollation("bn-x-icu");
            entity.HasIndex(x => x.CreatedAt);
        });
    }
}
