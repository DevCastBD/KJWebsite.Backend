using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public sealed class ContentDbContext(DbContextOptions<ContentDbContext> options) : DbContext(options)
{
    public DbSet<ContentItemEntity> Items => Set<ContentItemEntity>();
    public DbSet<ContentTranslationEntity> Translations => Set<ContentTranslationEntity>();
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<NewsArticleEntity> News => Set<NewsArticleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("content");

        // ICU collation is a PostgreSQL feature; Sqlite (test-only provider) has none. Titles mix Bengali and Latin text,
        // so sorting uses the Bengali ICU collation, as CTA does for names. P2 search builds on this choice.
        var useBengaliCollation = Database.IsNpgsql();
        if (useBengaliCollation)
        {
            modelBuilder.HasCollation("bn-x-icu", locale: "bn", provider: "icu");
        }

        modelBuilder.Entity<ContentItemEntity>(entity =>
        {
            entity.ToTable("content_items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Type).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Slug).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            // Optimistic concurrency for admin edits (If-Match): a save fails when the row changed since it was read.
            entity.Property(x => x.UpdatedAt).IsConcurrencyToken();
            entity.HasIndex(x => new { x.Type, x.Slug }).IsUnique();
            entity.HasIndex(x => new { x.Type, x.SortOrder });
            entity.HasOne(x => x.Project).WithOne().HasForeignKey<ProjectEntity>(p => p.ContentItemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.News).WithOne().HasForeignKey<NewsArticleEntity>(n => n.ContentItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContentTranslationEntity>(entity =>
        {
            entity.ToTable("content_translations");
            entity.HasKey(x => new { x.ContentItemId, x.Lang });
            entity.Property(x => x.ContentItemId).HasMaxLength(64);
            entity.Property(x => x.Lang).HasMaxLength(8);
            entity.Property(x => x.Title).IsRequired();
            entity.Property(x => x.AttributesJson).IsRequired();
            if (useBengaliCollation)
            {
                entity.Property(x => x.Title).UseCollation("bn-x-icu");
            }

            entity.HasOne<ContentItemEntity>().WithMany(i => i.Translations).HasForeignKey(t => t.ContentItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectEntity>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(x => x.ContentItemId);
            entity.Property(x => x.ContentItemId).HasMaxLength(64);
        });

        modelBuilder.Entity<NewsArticleEntity>(entity =>
        {
            entity.ToTable("news_articles");
            entity.HasKey(x => x.ContentItemId);
            entity.Property(x => x.ContentItemId).HasMaxLength(64);
        });
    }
}
