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
    public DbSet<ReceiptEntity> Receipts => Set<ReceiptEntity>();
    public DbSet<ReceiptVoidEntity> ReceiptVoids => Set<ReceiptVoidEntity>();
    public DbSet<ReceiptReissueEntity> ReceiptReissues => Set<ReceiptReissueEntity>();
    public DbSet<FinanceNotificationEntity> Notifications => Set<FinanceNotificationEntity>();
    public DbSet<StatementSendEntity> StatementSends => Set<StatementSendEntity>();
    public DbSet<DonorEntity> Donors => Set<DonorEntity>();
    public DbSet<DonorConsentEntity> DonorConsents => Set<DonorConsentEntity>();
    public DbSet<DonorSuppressionEntity> DonorSuppressions => Set<DonorSuppressionEntity>();
    public DbSet<DonorExportEntity> DonorExports => Set<DonorExportEntity>();
    public DbSet<CampaignEntity> Campaigns => Set<CampaignEntity>();
    public DbSet<ExpenseEntity> Expenses => Set<ExpenseEntity>();
    public DbSet<ExpenseReceiptEntity> ExpenseReceipts => Set<ExpenseReceiptEntity>();
    public DbSet<ExpenseApprovalEntity> ExpenseApprovals => Set<ExpenseApprovalEntity>();
    public DbSet<ExpenseDecisionEntity> ExpenseDecisions => Set<ExpenseDecisionEntity>();
    public DbSet<ExpensePaymentEntity> ExpensePayments => Set<ExpensePaymentEntity>();
    public DbSet<FiscalYearCloseEntity> FiscalYearCloses => Set<FiscalYearCloseEntity>();
    public DbSet<BudgetEntity> Budgets => Set<BudgetEntity>();
    public DbSet<BudgetAlertEntity> BudgetAlerts => Set<BudgetAlertEntity>();
    public DbSet<BankAccountEntity> BankAccounts => Set<BankAccountEntity>();
    public DbSet<BankStatementEntity> BankStatements => Set<BankStatementEntity>();
    public DbSet<BankLineEntity> BankLines => Set<BankLineEntity>();
    public DbSet<BankLineLinkEntity> BankLineLinks => Set<BankLineLinkEntity>();
    public DbSet<BankMatchEventEntity> BankMatchEvents => Set<BankMatchEventEntity>();
    public DbSet<BankRevealEntity> BankReveals => Set<BankRevealEntity>();
    public DbSet<ReportPackEntity> ReportPacks => Set<ReportPackEntity>();

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
            or ReceiptEntity or ReceiptVoidEntity or ReceiptReissueEntity or StatementSendEntity
            or DonorConsentEntity or DonorSuppressionEntity or DonorExportEntity
            or ExpenseEntity or ExpenseReceiptEntity or ExpenseApprovalEntity or ExpenseDecisionEntity or ExpensePaymentEntity
            or FiscalYearCloseEntity or BudgetAlertEntity
            or BankStatementEntity or BankLineEntity or BankMatchEventEntity or BankRevealEntity or ReportPackEntity
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
            entity.Property(x => x.CommunicationConsent).HasDefaultValue(false);
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

        modelBuilder.Entity<ReceiptEntity>(entity =>
        {
            entity.ToTable("receipts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.DonationId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Series).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Number).HasMaxLength(40).IsRequired();
            entity.Property(x => x.PrevHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Hash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DonorNameProtected).IsRequired();
            entity.Property(x => x.DonorEmailProtected).IsRequired();
            entity.Property(x => x.DonorKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.FeeAmount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.Method).HasMaxLength(16);
            entity.Property(x => x.DesignationType).HasMaxLength(16).IsRequired();
            entity.Property(x => x.DesignationId).HasMaxLength(100);
            entity.Property(x => x.Language).HasMaxLength(2).IsRequired();
            entity.Property(x => x.ItemDescription).HasMaxLength(500);
            entity.Property(x => x.ValuationBasis).HasMaxLength(16);
            entity.Property(x => x.OrganisationJson).IsRequired();
            // One receipt per donation, and one receipt per number: two concurrent issues cannot take the same one.
            entity.HasIndex(x => x.DonationId).IsUnique();
            entity.HasIndex(x => new { x.Series, x.Seq }).IsUnique();
            entity.HasIndex(x => x.Number).IsUnique();
            entity.HasIndex(x => new { x.Series, x.DonorKey });
        });

        modelBuilder.Entity<ReceiptVoidEntity>(entity =>
        {
            entity.ToTable("receipt_voids");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.ReceiptId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.VoidedBy).HasMaxLength(64);
            entity.HasIndex(x => x.ReceiptId).IsUnique();
        });

        modelBuilder.Entity<ReceiptReissueEntity>(entity =>
        {
            entity.ToTable("receipt_reissues");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.ReceiptId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.ReissuedBy).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.ReceiptId);
        });

        modelBuilder.Entity<FinanceNotificationEntity>(entity =>
        {
            entity.ToTable("notification_outbox");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Template).HasMaxLength(48).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(2).IsRequired();
            entity.Property(x => x.RecipientProtected).IsRequired();
            entity.Property(x => x.PayloadProtected).IsRequired();
            entity.HasIndex(x => x.SentAt);
        });

        modelBuilder.Entity<StatementSendEntity>(entity =>
        {
            entity.ToTable("statement_sends");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.DonorKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FiscalYear).HasMaxLength(16).IsRequired();
            entity.Property(x => x.QueuedBy).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.DonorKey, x.FiscalYear }).IsUnique();
        });

        modelBuilder.Entity<DonorEntity>(entity =>
        {
            entity.ToTable("donors");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.DonorKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.NameProtected).IsRequired();
            entity.Property(x => x.EmailProtected).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(2).IsRequired();
            // One donor per person: two gifts confirmed together cannot both create the record.
            entity.HasIndex(x => x.DonorKey).IsUnique();
        });

        modelBuilder.Entity<DonorConsentEntity>(entity =>
        {
            entity.ToTable("donor_consents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.DonorId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Purpose).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(24).IsRequired();
            entity.Property(x => x.RecordedBy).HasMaxLength(64);
            entity.HasIndex(x => new { x.DonorId, x.Purpose, x.Id });
        });

        modelBuilder.Entity<DonorSuppressionEntity>(entity =>
        {
            entity.ToTable("donor_suppressions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.DonorId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Template).HasMaxLength(48).IsRequired();
            entity.Property(x => x.Purpose).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => x.DonorId);
        });

        modelBuilder.Entity<DonorExportEntity>(entity =>
        {
            entity.ToTable("donor_exports");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Segment).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ConsentFilter).HasMaxLength(16);
            entity.Property(x => x.ExportedBy).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<CampaignEntity>(entity =>
        {
            entity.ToTable("campaigns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.TitleBn).HasMaxLength(200);
            entity.Property(x => x.GoalAmount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(64);
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<ExpenseEntity>(entity =>
        {
            entity.ToTable("expenses");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.Payee).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.CategoryKey).HasMaxLength(32).IsRequired();
            entity.Property(x => x.AccountCode).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.ProjectSlug).HasMaxLength(100);
            entity.Property(x => x.SubmittedBy).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SubmittedByName).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => x.ProjectSlug);
            entity.HasIndex(x => x.IncurredOn);
            if (Database.IsNpgsql())
            {
                entity.ToTable(t => t.HasCheckConstraint("ck_expenses_positive", "\"Amount\" > 0 AND \"RequiredApprovals\" >= 1"));
            }
        });

        modelBuilder.Entity<ExpenseReceiptEntity>(entity =>
        {
            entity.ToTable("expense_receipts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.ExpenseId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.StorageKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.UploadedBy).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.ExpenseId);
        });

        modelBuilder.Entity<ExpenseApprovalEntity>(entity =>
        {
            entity.ToTable("expense_approvals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.ExpenseId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ApprovedBy).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Note).HasMaxLength(500);
            // Nobody approves the same expense twice.
            entity.HasIndex(x => new { x.ExpenseId, x.ApprovedBy }).IsUnique();
        });

        modelBuilder.Entity<ExpenseDecisionEntity>(entity =>
        {
            entity.ToTable("expense_decisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.ExpenseId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Decision).HasMaxLength(16).IsRequired();
            entity.Property(x => x.DecidedBy).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Note).HasMaxLength(500);
            entity.HasIndex(x => x.ExpenseId).IsUnique();
        });

        modelBuilder.Entity<ExpensePaymentEntity>(entity =>
        {
            entity.ToTable("expense_payments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.ExpenseId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Method).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Reference).HasMaxLength(100);
            entity.Property(x => x.PaidBy).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.ExpenseId).IsUnique();
        });

        modelBuilder.Entity<FiscalYearCloseEntity>(entity =>
        {
            entity.ToTable("fiscal_year_closes");
            entity.HasKey(x => x.Label);
            entity.Property(x => x.Label).HasMaxLength(16);
            entity.Property(x => x.ClosedBy).HasMaxLength(64);
        });

        modelBuilder.Entity<BudgetEntity>(entity =>
        {
            entity.ToTable("budgets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.FiscalYear).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.ProjectSlug).HasMaxLength(100);
            entity.Property(x => x.CategoryKey).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PlannedAmount).HasPrecision(18, 2);
            entity.Property(x => x.CreatedBy).HasMaxLength(64);
            // One line per year, currency, project (or none) and category: SQL treats two NULLs as different, so the no-project case is a separate unique index.
            entity.HasIndex(x => new { x.FiscalYear, x.Currency, x.ProjectSlug, x.CategoryKey }).IsUnique().HasFilter("\"ProjectSlug\" IS NOT NULL");
            entity.HasIndex(x => new { x.FiscalYear, x.Currency, x.CategoryKey }).IsUnique().HasFilter("\"ProjectSlug\" IS NULL");
            if (Database.IsNpgsql())
            {
                entity.ToTable(t => t.HasCheckConstraint("ck_budgets_positive", "\"PlannedAmount\" > 0"));
            }
        });

        modelBuilder.Entity<BudgetAlertEntity>(entity =>
        {
            entity.ToTable("budget_alerts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.BudgetId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.PlannedAmount).HasPrecision(18, 2);
            entity.Property(x => x.ActualAmount).HasPrecision(18, 2);
            entity.HasIndex(x => new { x.BudgetId, x.ThresholdPercent }).IsUnique();
        });

        modelBuilder.Entity<BankAccountEntity>(entity =>
        {
            entity.ToTable("bank_accounts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.BankName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.NumberProtected).IsRequired();
            entity.Property(x => x.Last4).HasMaxLength(4).IsRequired();
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.LedgerAccountId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.CsvMapping).HasMaxLength(2000);
            entity.Property(x => x.CreatedBy).HasMaxLength(64);
            entity.HasOne(x => x.LedgerAccount).WithMany().HasForeignKey(x => x.LedgerAccountId).OnDelete(DeleteBehavior.Restrict);
            // Matching works on the ledger account, so two live bank accounts may not share one.
            entity.HasIndex(x => x.LedgerAccountId).IsUnique().HasFilter("\"ArchivedAt\" IS NULL");
        });

        modelBuilder.Entity<BankStatementEntity>(entity =>
        {
            entity.ToTable("bank_statements");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.BankAccountId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.FileHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Format).HasMaxLength(8).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(200);
            entity.Property(x => x.ImportedBy).HasMaxLength(64);
            entity.HasIndex(x => new { x.BankAccountId, x.FileHash }).IsUnique();
        });

        modelBuilder.Entity<BankLineEntity>(entity =>
        {
            entity.ToTable("bank_lines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.BankAccountId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Reference).HasMaxLength(100);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Balance).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.BankAccountId, x.Fingerprint, x.Occurrence }).IsUnique();
            entity.HasIndex(x => x.StatementId);
            entity.HasOne(x => x.Statement).WithMany().HasForeignKey(x => x.StatementId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BankLineLinkEntity>(entity =>
        {
            entity.ToTable("bank_line_links");
            entity.HasKey(x => x.LineId);
            entity.Property(x => x.LineId).ValueGeneratedNever();
            entity.Property(x => x.State).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.By).HasMaxLength(64);
            // One ledger entry proves at most one bank line.
            entity.HasIndex(x => x.LedgerEntryId).IsUnique().HasFilter("\"LedgerEntryId\" IS NOT NULL");
        });

        modelBuilder.Entity<BankMatchEventEntity>(entity =>
        {
            entity.ToTable("bank_match_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Action).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.By).HasMaxLength(64);
            entity.HasIndex(x => x.LineId);
        });

        modelBuilder.Entity<BankRevealEntity>(entity =>
        {
            entity.ToTable("bank_reveals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.BankAccountId).HasMaxLength(40).IsRequired();
            entity.Property(x => x.By).HasMaxLength(64);
        });

        modelBuilder.Entity<ReportPackEntity>(entity =>
        {
            entity.ToTable("report_packs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(40);
            entity.Property(x => x.Month).HasMaxLength(7).IsRequired();
            entity.Property(x => x.EntityName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.StorageKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.GeneratedBy).HasMaxLength(64);
            entity.HasIndex(x => x.Month);
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
