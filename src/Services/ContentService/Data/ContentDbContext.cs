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
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<EventSessionEntity> EventSessions => Set<EventSessionEntity>();
    public DbSet<EventRegistrationEntity> EventRegistrations => Set<EventRegistrationEntity>();
    public DbSet<EventNotificationEntity> EventNotifications => Set<EventNotificationEntity>();
    public DbSet<EventCertificateEntity> EventCertificates => Set<EventCertificateEntity>();
    public DbSet<EventFeedTokenEntity> EventFeedTokens => Set<EventFeedTokenEntity>();
    public DbSet<NewsletterSubscriberEntity> NewsletterSubscribers => Set<NewsletterSubscriberEntity>();
    public DbSet<NewsletterEventEntity> NewsletterEvents => Set<NewsletterEventEntity>();
    public DbSet<NewsletterMessageEntity> NewsletterMessages => Set<NewsletterMessageEntity>();
    public DbSet<NewsletterSegmentEntity> NewsletterSegments => Set<NewsletterSegmentEntity>();
    public DbSet<NewsletterTemplateEntity> NewsletterTemplates => Set<NewsletterTemplateEntity>();
    public DbSet<NewsletterCampaignEntity> NewsletterCampaigns => Set<NewsletterCampaignEntity>();
    public DbSet<NotificationTemplateEntity> NotificationTemplates => Set<NotificationTemplateEntity>();
    public DbSet<NotificationSendEntity> NotificationSends => Set<NotificationSendEntity>();
    public DbSet<NotificationPreferenceEntity> NotificationPreferences => Set<NotificationPreferenceEntity>();
    public DbSet<InAppNotificationEntity> InAppNotifications => Set<InAppNotificationEntity>();
    public DbSet<SmsMessageEntity> SmsMessages => Set<SmsMessageEntity>();
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();
    public DbSet<EventFeedbackFormEntity> EventFeedbackForms => Set<EventFeedbackFormEntity>();
    public DbSet<EventFeedbackResponseEntity> EventFeedbackResponses => Set<EventFeedbackResponseEntity>();
    public DbSet<TagEntity> Tags => Set<TagEntity>();
    public DbSet<TagNameEntity> TagNames => Set<TagNameEntity>();
    public DbSet<ContentItemTagEntity> ItemTags => Set<ContentItemTagEntity>();
    public DbSet<ContentRelationEntity> Relations => Set<ContentRelationEntity>();
    public DbSet<RedirectEntity> Redirects => Set<RedirectEntity>();
    public DbSet<ContentRevisionEntity> Revisions => Set<ContentRevisionEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<SearchTermEntity> SearchTerms => Set<SearchTermEntity>();
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
            entity.HasOne(x => x.Event).WithOne().HasForeignKey<EventEntity>(n => n.ContentItemId).OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<EventEntity>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(x => x.ContentItemId);
            entity.Property(x => x.ContentItemId).HasMaxLength(64);
            entity.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OnlineUrl).HasMaxLength(512).IsRequired();
            entity.Property(x => x.RecurrenceRule).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ExceptionsJson).IsRequired();
            entity.Property(x => x.SeriesKey).HasMaxLength(120).IsRequired();
            entity.HasIndex(x => x.SeriesKey);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired().HasDefaultValue(string.Empty);
            entity.Property(x => x.RegistrationOpen).HasDefaultValue(true);
            entity.Property(x => x.RegistrationVersion).IsConcurrencyToken();
        });

        modelBuilder.Entity<PressCoverageEntity>(entity =>
        {
            entity.ToTable("press_coverage");
            entity.HasKey(x => x.ContentItemId);
            entity.Property(x => x.ContentItemId).HasMaxLength(64);
            entity.HasOne<ContentItemEntity>().WithOne().HasForeignKey<PressCoverageEntity>(x => x.ContentItemId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.Outlet).HasMaxLength(200).IsRequired();
            entity.Property(x => x.OutletLogoUrl).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ExternalUrl).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ClippingUrl).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(8).IsRequired();
            entity.Property(x => x.ReproductionPermission).HasMaxLength(16).IsRequired();
            entity.Property(x => x.PermissionNote).HasMaxLength(500).IsRequired();
            entity.HasIndex(x => new { x.Featured, x.FeaturedOrder });
        });

        modelBuilder.Entity<EventSessionEntity>(entity =>
        {
            entity.ToTable("event_sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.ContentItemId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TitlesJson).IsRequired();
            entity.Property(x => x.Speaker).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Room).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.ContentItemId, x.Position }).IsUnique();
            entity.HasOne<ContentItemEntity>().WithMany().HasForeignKey(x => x.ContentItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EventRegistrationEntity>(entity =>
        {
            entity.ToTable("event_registrations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.EventItemId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.TicketCode).HasMaxLength(40).IsRequired();
            entity.Property(x => x.MemberId).HasMaxLength(64);
            entity.Property(x => x.NameProtected).IsRequired();
            entity.Property(x => x.EmailProtected).IsRequired();
            entity.Property(x => x.EmailKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(8).IsRequired();
            entity.Property(x => x.DecisionReason).HasMaxLength(500);
            entity.Property(x => x.PaymentReference).HasMaxLength(120);
            entity.Property(x => x.CheckInSource).HasMaxLength(16);
            entity.HasIndex(x => x.TicketCode).IsUnique();
            entity.HasIndex(x => new { x.EventItemId, x.OccurrenceStartUtc, x.Status });
            entity.HasIndex(x => new { x.EventItemId, x.EmailKey });
            entity.HasOne<ContentItemEntity>().WithMany().HasForeignKey(x => x.EventItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EventNotificationEntity>(entity =>
        {
            entity.ToTable("event_notifications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RegistrationId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Template).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(8).IsRequired();
            entity.Property(x => x.RecipientProtected).IsRequired();
            entity.Property(x => x.PayloadJson).IsRequired();
            entity.HasIndex(x => x.SentAt);
        });

        modelBuilder.Entity<NewsletterSubscriberEntity>(entity =>
        {
            entity.ToTable("newsletter_subscribers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Email).HasMaxLength(254).IsRequired();
            entity.Property(x => x.Lang).HasMaxLength(8).IsRequired();
            entity.Property(x => x.TopicsJson).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(40).IsRequired();
            entity.Property(x => x.SuppressionReason).HasMaxLength(40);
            entity.HasIndex(x => x.Email).IsUnique();
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<NewsletterEventEntity>(entity =>
        {
            entity.ToTable("newsletter_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SubscriberId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Detail).HasMaxLength(500);
            entity.HasIndex(x => x.SubscriberId);
        });

        modelBuilder.Entity<NewsletterMessageEntity>(entity =>
        {
            entity.ToTable("newsletter_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SubscriberId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ToEmail).HasMaxLength(254).IsRequired();
            entity.Property(x => x.Kind).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Lang).HasMaxLength(8).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Body).IsRequired();
            entity.Property(x => x.ListUnsubscribeUrl).HasMaxLength(500);
            entity.Property(x => x.CampaignId).HasMaxLength(64);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Error).HasMaxLength(300);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CampaignId);
        });

        modelBuilder.Entity<NewsletterTemplateEntity>(entity =>
        {
            entity.ToTable("newsletter_templates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.SubjectBn).HasMaxLength(300).IsRequired();
            entity.Property(x => x.SubjectEn).HasMaxLength(300).IsRequired();
            entity.Property(x => x.BodyBn).IsRequired();
            entity.Property(x => x.BodyEn).IsRequired();
        });

        modelBuilder.Entity<NewsletterCampaignEntity>(entity =>
        {
            entity.ToTable("newsletter_campaigns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.SegmentId).HasMaxLength(64);
            entity.Property(x => x.SubjectBn).HasMaxLength(300).IsRequired();
            entity.Property(x => x.SubjectEn).HasMaxLength(300).IsRequired();
            entity.Property(x => x.BodyBn).IsRequired();
            entity.Property(x => x.BodyEn).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
        });

        modelBuilder.Entity<NotificationTemplateEntity>(entity =>
        {
            entity.ToTable("notification_templates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Key).HasMaxLength(60).IsRequired();
            entity.Property(x => x.Lang).HasMaxLength(8).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Body).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.Key, x.Lang, x.Version }).IsUnique();
        });

        modelBuilder.Entity<NotificationSendEntity>(entity =>
        {
            entity.ToTable("notification_sends");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(60).IsRequired();
            entity.Property(x => x.Lang).HasMaxLength(8).IsRequired();
            entity.Property(x => x.ToEmail).HasMaxLength(254).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Body).IsRequired();
            entity.Property(x => x.BodyHtml).IsRequired();
            entity.HasIndex(x => x.Key);
        });

        modelBuilder.Entity<NotificationPreferenceEntity>(entity =>
        {
            entity.ToTable("notification_preferences");
            entity.HasKey(x => x.Subject);
            entity.Property(x => x.Subject).HasMaxLength(64);
            entity.Property(x => x.Phone).HasMaxLength(16);
            entity.Property(x => x.Lang).HasMaxLength(8).IsRequired();
        });

        modelBuilder.Entity<InAppNotificationEntity>(entity =>
        {
            entity.ToTable("in_app_notifications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Subject).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Key).HasMaxLength(60).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Body).IsRequired();
            entity.HasIndex(x => x.Subject);
        });

        modelBuilder.Entity<SmsMessageEntity>(entity =>
        {
            entity.ToTable("sms_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Subject).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ToPhone).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Key).HasMaxLength(60).IsRequired();
            entity.Property(x => x.Lang).HasMaxLength(8).IsRequired();
            entity.Property(x => x.Body).IsRequired();
            entity.Property(x => x.Encoding).HasMaxLength(8).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Error).HasMaxLength(300);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CreatedUnix);
        });

        modelBuilder.Entity<PushSubscriptionEntity>(entity =>
        {
            entity.ToTable("push_subscriptions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Subject).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Endpoint).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.P256dh).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Auth).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.Endpoint).IsUnique();
            entity.HasIndex(x => x.Subject);
        });

        modelBuilder.Entity<NewsletterSegmentEntity>(entity =>
        {
            entity.ToTable("newsletter_segments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.FilterJson).IsRequired();
        });

        modelBuilder.Entity<EventFeedTokenEntity>(entity =>
        {
            entity.ToTable("event_feed_tokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Subject).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.Subject);
        });

        modelBuilder.Entity<EventCertificateEntity>(entity =>
        {
            entity.ToTable("event_certificates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.RegistrationId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.EventItemId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Number).HasMaxLength(40).IsRequired();
            entity.Property(x => x.NameProtected).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(8).IsRequired();
            entity.Property(x => x.IssuedBy).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.RegistrationId).IsUnique();
            entity.HasIndex(x => x.Number).IsUnique();
            entity.HasOne<EventRegistrationEntity>().WithMany().HasForeignKey(x => x.RegistrationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EventFeedbackFormEntity>(entity =>
        {
            entity.ToTable("event_feedback_forms");
            entity.HasKey(x => x.EventItemId);
            entity.Property(x => x.EventItemId).HasMaxLength(64);
            entity.Property(x => x.QuestionsJson).IsRequired();
            entity.HasOne<ContentItemEntity>().WithMany().HasForeignKey(x => x.EventItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EventFeedbackResponseEntity>(entity =>
        {
            entity.ToTable("event_feedback_responses");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.EventItemId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RegistrationId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AnswersJson).IsRequired();
            entity.HasIndex(x => x.RegistrationId).IsUnique();
            entity.HasIndex(x => x.EventItemId);
            entity.HasOne<EventRegistrationEntity>().WithMany().HasForeignKey(x => x.RegistrationId).OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<SearchTermEntity>(entity =>
        {
            entity.ToTable("search_terms");
            entity.HasKey(x => new { x.ContentItemId, x.Lang, x.Term });
            entity.Property(x => x.ContentItemId).HasMaxLength(64);
            entity.Property(x => x.Lang).HasMaxLength(8);
            entity.Property(x => x.Term).HasMaxLength(64);
            entity.HasIndex(x => new { x.Lang, x.Term });
            entity.HasOne<ContentItemEntity>().WithMany().HasForeignKey(x => x.ContentItemId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
