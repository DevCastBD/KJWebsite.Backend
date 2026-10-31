using FinanceService.Data.Entities;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks.Auditing;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Data;

public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : DbContext(options)
{
    public DbSet<DonationEntity> Donations => Set<DonationEntity>();
    public DbSet<DonationEventEntity> DonationEvents => Set<DonationEventEntity>();
    public DbSet<RecurringPlanEntity> RecurringPlans => Set<RecurringPlanEntity>();
    public DbSet<PaymentAttemptEntity> PaymentAttempts => Set<PaymentAttemptEntity>();
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<PostingEntity> Postings => Set<PostingEntity>();
    public DbSet<LedgerEntryEntity> LedgerEntries => Set<LedgerEntryEntity>();
    public DbSet<PeriodCloseEntity> PeriodCloses => Set<PeriodCloseEntity>();
    public DbSet<PaymentEventEntity> PaymentEvents => Set<PaymentEventEntity>();
    public DbSet<WebhookRejectionEntity> WebhookRejections => Set<WebhookRejectionEntity>();
    public DbSet<PayoutEntity> Payouts => Set<PayoutEntity>();
    public DbSet<PayoutLineEntity> PayoutLines => Set<PayoutLineEntity>();
    public DbSet<PaymentSettlementEntity> PaymentSettlements => Set<PaymentSettlementEntity>();
    public DbSet<ProviderNoticeEntity> ProviderNotices => Set<ProviderNoticeEntity>();
    public DbSet<OfflineRecordEntity> OfflineRecords => Set<OfflineRecordEntity>();
    public DbSet<OfflineDecisionEntity> OfflineDecisions => Set<OfflineDecisionEntity>();
    public DbSet<OfflineProofEntity> OfflineProofs => Set<OfflineProofEntity>();

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

    // Financial records are append-only: a correction is a new reversing entry. PostgreSQL enforces the same with triggers (see the migrations),
    // so a bulk ExecuteUpdate or a hand-written statement is refused too. Nothing may be saved that would break the ledger's rules.
    private void RefuseLedgerEdits()
    {
        ChangeTracker.DetectChanges();
        var offender = ChangeTracker.Entries().FirstOrDefault(e =>
            e.Entity is DonationEntity or DonationEventEntity or RecurringPlanEntity or PaymentAttemptEntity or PostingEntity or LedgerEntryEntity or PeriodCloseEntity
            or PaymentEventEntity or WebhookRejectionEntity or PayoutEntity or PayoutLineEntity or PaymentSettlementEntity or ProviderNoticeEntity
            or OfflineRecordEntity or OfflineDecisionEntity or OfflineProofEntity
            && e.State is EntityState.Modified or EntityState.Deleted);
        if (offender is not null)
        {
            throw new InvalidOperationException($"{offender.Metadata.ClrType.Name} records are append-only: post a reversing entry instead of changing or deleting one.");
        }

        // An account is never deleted, and its code and type (which postings rely on) never change; renaming and archiving are fine.
        foreach (var entry in ChangeTracker.Entries<AccountEntity>())
        {
            if (entry.State == EntityState.Deleted
                || (entry.State == EntityState.Modified && (entry.Property(a => a.Code).IsModified || entry.Property(a => a.Type).IsModified)))
            {
                throw new InvalidOperationException("An account cannot be deleted, and its code and type cannot change.");
            }
        }

        // A posting is written whole and balanced: lines may only be added together with their new posting, and must net to zero per currency.
        var added = ChangeTracker.Entries<LedgerEntryEntity>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (added.Any(l => l.Posting is null || ChangeTracker.Entries<PostingEntity>().All(p => p.Entity != l.Posting || p.State != EntityState.Added)))
        {
            throw new InvalidOperationException("Ledger lines can only be added together with their own new posting.");
        }

        foreach (var posting in ChangeTracker.Entries<PostingEntity>().Where(e => e.State == EntityState.Added).Select(e => e.Entity))
        {
            var lines = added.Where(l => l.Posting == posting).ToList();
            var valid = lines.Count >= 2
                && lines.All(l => (l.Debit > 0m) != (l.Credit > 0m) && l.Debit >= 0m && l.Credit >= 0m)
                && PostingRules.Balanced(lines.Select(l => (l.Currency, l.Debit, l.Credit)));
            if (!valid)
            {
                throw new InvalidOperationException("A posting needs two or more one-sided lines whose debits equal their credits in every currency.");
            }
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

        modelBuilder.Entity<PaymentAttemptEntity>(entity =>
        {
            entity.ToTable("payment_attempts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.DonationId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Gateway).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ProviderPaymentId).HasMaxLength(200);
            entity.Property(x => x.ErrorCode).HasMaxLength(64);
            entity.HasIndex(x => new { x.DonationId, x.Id });
            // Confirmation looks a payment up by the gateway's id.
            entity.HasIndex(x => new { x.Gateway, x.ProviderPaymentId });
        });

        modelBuilder.Entity<AccountEntity>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.Code).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<PostingEntity>(entity =>
        {
            entity.ToTable("postings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.PeriodKey).HasMaxLength(7).IsRequired();
            entity.Property(x => x.FiscalYear).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Memo).HasMaxLength(500).IsRequired();
            entity.Property(x => x.SourceType).HasMaxLength(24).IsRequired();
            entity.Property(x => x.SourceId).HasMaxLength(100);
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.CreatedBy).HasMaxLength(64);
            // One posting per cause: this is what makes a replayed webhook post exactly once.
            entity.HasIndex(x => new { x.SourceType, x.SourceId }).IsUnique().HasFilter("\"SourceId\" IS NOT NULL");
            // One reversal per posting.
            entity.HasIndex(x => x.ReversesPostingId).IsUnique().HasFilter("\"ReversesPostingId\" IS NOT NULL");
            entity.HasIndex(x => x.PostedOn);
            entity.HasIndex(x => x.PeriodKey);
            entity.HasMany(x => x.Entries).WithOne(x => x.Posting).HasForeignKey(x => x.PostingId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerEntryEntity>(entity =>
        {
            entity.ToTable("ledger_entries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.AccountId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Debit).HasPrecision(18, 2);
            entity.Property(x => x.Credit).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.AccountId, x.PostingId });
            entity.HasIndex(x => x.PostingId);
            if (Database.IsNpgsql())
            {
                entity.ToTable(t => t.HasCheckConstraint("ck_ledger_entries_one_side",
                    "\"Debit\" >= 0 AND \"Credit\" >= 0 AND ((\"Debit\" > 0 AND \"Credit\" = 0) OR (\"Debit\" = 0 AND \"Credit\" > 0))"));
            }
        });

        modelBuilder.Entity<PaymentEventEntity>(entity =>
        {
            entity.ToTable("payment_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Gateway).HasMaxLength(32).IsRequired();
            entity.Property(x => x.EventKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.DonationId).HasMaxLength(40);
            entity.Property(x => x.Outcome).HasMaxLength(24).IsRequired();
            // The replay guard: one record per delivery.
            entity.HasIndex(x => new { x.Gateway, x.EventKey }).IsUnique();
        });

        modelBuilder.Entity<WebhookRejectionEntity>(entity =>
        {
            entity.ToTable("webhook_rejections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Gateway).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ReasonCode).HasMaxLength(32).IsRequired();
            entity.Property(x => x.SourceIp).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Reference).HasMaxLength(64);
            entity.HasIndex(x => x.Id);
        });

        modelBuilder.Entity<PaymentSettlementEntity>(entity =>
        {
            entity.ToTable("payment_settlements");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.DonationId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Gateway).HasMaxLength(32).IsRequired();
            entity.Property(x => x.TransactionId).HasMaxLength(200);
            entity.Property(x => x.SettledAmount).HasPrecision(18, 2);
            entity.Property(x => x.SettledCurrency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.Fee).HasPrecision(18, 2);
            entity.Property(x => x.ExchangeRate).HasPrecision(18, 8);
            entity.HasIndex(x => x.DonationId).IsUnique();
            entity.HasIndex(x => new { x.Gateway, x.TransactionId });
        });

        modelBuilder.Entity<ProviderNoticeEntity>(entity =>
        {
            entity.ToTable("provider_notices");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Gateway).HasMaxLength(32).IsRequired();
            entity.Property(x => x.EventKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            entity.Property(x => x.DonationId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.TransactionId).HasMaxLength(200);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.HasIndex(x => new { x.Gateway, x.EventKey }).IsUnique();
            entity.HasIndex(x => x.DonationId);
        });

        modelBuilder.Entity<OfflineRecordEntity>(entity =>
        {
            entity.ToTable("offline_records");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.DonationId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Method).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ReferenceNo).HasMaxLength(100);
            entity.Property(x => x.Note).HasMaxLength(1000);
            entity.Property(x => x.ItemDescription).HasMaxLength(500);
            entity.Property(x => x.ValuationBasis).HasMaxLength(16);
            entity.Property(x => x.ValuationNote).HasMaxLength(500);
            entity.Property(x => x.RecordedBy).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.DonationId).IsUnique();
            entity.HasIndex(x => x.ReceivedOn);
        });

        modelBuilder.Entity<OfflineDecisionEntity>(entity =>
        {
            entity.ToTable("offline_decisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.OfflineRecordId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Decision).HasMaxLength(16).IsRequired();
            entity.Property(x => x.DecidedBy).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500);
            // One decision per record: two approvers cannot both decide it.
            entity.HasIndex(x => x.OfflineRecordId).IsUnique();
        });

        modelBuilder.Entity<OfflineProofEntity>(entity =>
        {
            entity.ToTable("offline_proofs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.OfflineRecordId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.StorageKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.UploadedBy).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.OfflineRecordId);
        });

        modelBuilder.Entity<PayoutEntity>(entity =>
        {
            entity.ToTable("payouts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Gateway).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PayoutRef).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ImportedBy).HasMaxLength(64);
            entity.HasIndex(x => new { x.Gateway, x.PayoutRef }).IsUnique();
            entity.HasMany(x => x.Lines).WithOne(x => x.Payout).HasForeignKey(x => x.PayoutId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PayoutLineEntity>(entity =>
        {
            entity.ToTable("payout_lines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Reference).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DonationId).HasMaxLength(40);
            entity.Property(x => x.Gross).HasPrecision(18, 2);
            entity.Property(x => x.Fee).HasPrecision(18, 2);
            entity.Property(x => x.Net).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.MatchStatus).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(32);
            entity.HasOne(x => x.Posting).WithMany().HasForeignKey(x => x.PostingId).OnDelete(DeleteBehavior.Restrict);
            // A donation is settled by at most one payout.
            entity.HasIndex(x => x.DonationId).IsUnique().HasFilter("\"MatchStatus\" = 'matched'");
            entity.HasIndex(x => x.PayoutId);
        });

        modelBuilder.Entity<PeriodCloseEntity>(entity =>
        {
            entity.ToTable("period_closes");
            entity.HasKey(x => x.PeriodKey);
            entity.Property(x => x.PeriodKey).HasMaxLength(7);
            entity.Property(x => x.ClosedBy).HasMaxLength(64);
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
