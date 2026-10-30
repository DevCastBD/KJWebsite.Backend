using ContentService.Data.Entities;
using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public sealed class ContentDbContext(DbContextOptions<ContentDbContext> options) : DbContext(options)
{
    public DbSet<ContentItemEntity> Items => Set<ContentItemEntity>();
    public DbSet<ContentTranslationEntity> Translations => Set<ContentTranslationEntity>();
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<NewsArticleEntity> News => Set<NewsArticleEntity>();
    public DbSet<TagEntity> Tags => Set<TagEntity>();
    public DbSet<TagNameEntity> TagNames => Set<TagNameEntity>();
    public DbSet<ContentItemTagEntity> ItemTags => Set<ContentItemTagEntity>();
    public DbSet<ContentRelationEntity> Relations => Set<ContentRelationEntity>();
    public DbSet<RedirectEntity> Redirects => Set<RedirectEntity>();
    public DbSet<ContentRevisionEntity> Revisions => Set<ContentRevisionEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<FeatureFlagEntity> FeatureFlags => Set<FeatureFlagEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("content");
        modelBuilder.ConfigureAudit();

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
            entity.Property(x => x.ImageUrl).HasMaxLength(512).HasDefaultValue(string.Empty);
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

        modelBuilder.Entity<TagEntity>(entity =>
        {
            entity.ToTable("tags");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Slug).HasMaxLength(120).IsRequired();
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasMany(x => x.Names).WithOne().HasForeignKey(n => n.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TagNameEntity>(entity =>
        {
            entity.ToTable("tag_names");
            entity.HasKey(x => new { x.TagId, x.Lang });
            entity.Property(x => x.TagId).HasMaxLength(64);
            entity.Property(x => x.Lang).HasMaxLength(8);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.Lang, x.NormalizedName }).IsUnique();
        });

        modelBuilder.Entity<ContentItemTagEntity>(entity =>
        {
            entity.ToTable("content_item_tags");
            entity.HasKey(x => new { x.ContentItemId, x.TagId });
            entity.Property(x => x.ContentItemId).HasMaxLength(64);
            entity.Property(x => x.TagId).HasMaxLength(64);
            entity.HasIndex(x => x.TagId);
            entity.HasOne<ContentItemEntity>().WithMany(i => i.TagLinks).HasForeignKey(x => x.ContentItemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContentRelationEntity>(entity =>
        {
            entity.ToTable("content_relations");
            entity.HasKey(x => new { x.FromItemId, x.ToItemId });
            entity.Property(x => x.FromItemId).HasMaxLength(64);
            entity.Property(x => x.ToItemId).HasMaxLength(64);
            entity.HasOne<ContentItemEntity>().WithMany(i => i.RelatedLinks).HasForeignKey(x => x.FromItemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.To).WithMany().HasForeignKey(x => x.ToItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RedirectEntity>(entity =>
        {
            entity.ToTable("redirects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.FromPath).HasMaxLength(512).IsRequired();
            entity.Property(x => x.ToPath).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => x.FromPath).IsUnique();
            entity.HasIndex(x => x.ToPath);
        });

        modelBuilder.Entity<ContentRevisionEntity>(entity =>
        {
            entity.ToTable("content_revisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.ContentItemId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SnapshotJson).IsRequired();
            entity.HasIndex(x => new { x.ContentItemId, x.Number }).IsUnique();
            entity.HasOne<ContentItemEntity>().WithMany().HasForeignKey(x => x.ContentItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SettingEntity>(entity =>
        {
            entity.ToTable("settings");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(100);
            entity.Property(x => x.ValueJson).IsRequired();
            entity.Property(x => x.UpdatedAt).IsConcurrencyToken();
        });

        modelBuilder.Entity<FeatureFlagEntity>(entity =>
        {
            entity.ToTable("feature_flags");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(100);
            entity.Property(x => x.Description).HasMaxLength(500).IsRequired();
        });
    }
}
