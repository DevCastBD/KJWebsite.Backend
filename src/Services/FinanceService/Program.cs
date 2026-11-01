using FinanceService.Banking;
using FinanceService.Budgets;
using FinanceService.Campaigns;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Donors;
using FinanceService.Expenses;
using FinanceService.Ledger;
using FinanceService.Offline;
using FinanceService.Payments;
using FinanceService.Receipts;
using FinanceService.Reconciliation;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Idempotency;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "Finance Service API";
        document.Info.Description = "Donations: the public donation form and status lookup, and the finance back office. Money is always a decimal string with an explicit currency; financial records are append-only.";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, ct) =>
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IdempotentEndpointMetadata>().Any())
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new Microsoft.OpenApi.OpenApiParameter
            {
                Name = IdempotencyExtensions.HeaderName,
                In = Microsoft.OpenApi.ParameterLocation.Header,
                Required = false,
                Description = "Optional. A repeat with the same key and body returns the original response (header `Idempotency-Replayed: true`) and creates nothing new; the same key with a different body is 422; a repeat while the first is running is 409. Keys are remembered for 24 hours.",
                Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String }
            });
        }

        return Task.CompletedTask;
    });
});

builder.Services.AddDbContext<FinanceDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("FinanceDb")
        ?? (configuration.GetValue<bool>("OpenApi:Export") ? "Host=localhost;Database=finance" : throw new InvalidOperationException("ConnectionStrings:FinanceDb is required."));
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "finance"));
    options.AddInterceptors(services.GetRequiredService<AuditInterceptor>());
});
builder.Services.AddKjAuditing("finance", audit => audit
    // Donor details stay out of the log; amounts, designation and state are what an auditor needs.
    .Audit<DonationEntity>(nameof(DonationEntity.DonorNameProtected), nameof(DonationEntity.DonorEmailProtected), nameof(DonationEntity.DonorPhoneProtected), nameof(DonationEntity.Dedication))
    .Audit<DonationEventEntity>()
    .Audit<AccountEntity>()
    .Audit<PostingEntity>()
    .Audit<LedgerEntryEntity>()
    .Audit<PeriodCloseEntity>()
    .Audit<OfflineRecordEntity>()
    .Audit<OfflineDecisionEntity>()
    .Audit<OfflineProofEntity>()
    .Audit<ReceiptEntity>(nameof(ReceiptEntity.DonorNameProtected), nameof(ReceiptEntity.DonorEmailProtected), nameof(ReceiptEntity.DonorKey))
    .Audit<ReceiptVoidEntity>()
    .Audit<ReceiptReissueEntity>()
    .Audit<StatementSendEntity>()
    .Audit<DonorEntity>(nameof(DonorEntity.NameProtected), nameof(DonorEntity.EmailProtected), nameof(DonorEntity.PhoneProtected), nameof(DonorEntity.DonorKey))
    .Audit<DonorConsentEntity>()
    .Audit<DonorExportEntity>()
    .Audit<CampaignEntity>()
    .Audit<ExpenseEntity>()
    .Audit<ExpenseApprovalEntity>()
    .Audit<ExpenseDecisionEntity>()
    .Audit<ExpensePaymentEntity>()
    .Audit<ExpenseReceiptEntity>()
    .Audit<FiscalYearCloseEntity>()
    .Audit<BudgetEntity>()
    .Audit<BudgetAlertEntity>()
    .Audit<BankAccountEntity>(nameof(BankAccountEntity.NumberProtected))
    .Audit<BankStatementEntity>()
    .Audit<BankLineEntity>()
    .Audit<BankLineLinkEntity>()
    .Audit<BankMatchEventEntity>()
    .Audit<BankRevealEntity>()
    .Audit<PayoutEntity>()
    .Audit<PayoutLineEntity>()
    .Audit<RecurringPlanEntity>(nameof(RecurringPlanEntity.DonorNameProtected), nameof(RecurringPlanEntity.DonorEmailProtected), nameof(RecurringPlanEntity.DonorPhoneProtected), nameof(RecurringPlanEntity.Dedication)));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<DonationOptions>(builder.Configuration.GetSection(DonationOptions.SectionName));
// Persist the key ring (DataProtection:KeysPath) in any deployment: donor details encrypted by one instance must open on the next.
var dataProtection = builder.Services.AddDataProtection(o => o.ApplicationDiscriminator = "KJWebsite.Finance");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddSingleton<IDonorProtector, DataProtectionDonorProtector>();
builder.Services.Configure<LedgerOptions>(builder.Configuration.GetSection(LedgerOptions.SectionName));
builder.Configuration.GetSection(LedgerOptions.SectionName).Get<LedgerOptions>()?.Validate();
builder.Services.Configure<ProofOptions>(builder.Configuration.GetSection(ProofOptions.SectionName));
builder.Services.AddSingleton<IProofStore, LocalDiskProofStore>();
builder.Services.AddScoped<OfflineDonationService>();
builder.Services.Configure<ReceiptOptions>(builder.Configuration.GetSection(ReceiptOptions.SectionName));
builder.Services.AddSingleton<DonorKeyHasher>();
builder.Services.AddScoped<ReceiptService>();
builder.Services.Configure<DonorOptions>(builder.Configuration.GetSection(DonorOptions.SectionName));
builder.Services.AddScoped<DonorMessenger>();
builder.Services.AddScoped<DonorService>();
builder.Services.AddScoped<DonorData>();
builder.Services.Configure<ExpenseOptions>(builder.Configuration.GetSection(ExpenseOptions.SectionName));
builder.Services.AddScoped<ExpenseService>();
builder.Services.Configure<BudgetOptions>(builder.Configuration.GetSection(BudgetOptions.SectionName));
builder.Services.Configure<BankingOptions>(builder.Configuration.GetSection(BankingOptions.SectionName));
builder.Services.AddScoped<BankingService>();
builder.Services.AddScoped<BudgetMath>();
builder.Services.AddScoped<BudgetAlertService>();
builder.Services.Configure<CampaignOptions>(builder.Configuration.GetSection(CampaignOptions.SectionName));
builder.Services.AddSingleton<CampaignProgressCache>();
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<DonationConfirmation>();
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));
// A new provider is one class plus one line here; nothing else in the service changes. HTTP logging is off for gateway clients so a URL or header can never put a credential in a log.
builder.Services.AddPaymentGateway<SslCommerzGateway>(SslCommerzGateway.ClientName);
builder.Services.AddPaymentGateway<BkashGateway>(BkashGateway.ClientName);
builder.Services.AddPaymentGateway<NagadGateway>(NagadGateway.ClientName);
builder.Services.AddPaymentGateway<StripeGateway>(StripeGateway.ClientName);
builder.Services.AddPaymentGateway<PayPalGateway>(PayPalGateway.ClientName);
builder.Services.AddSingleton<PaymentGatewayRegistry>();
builder.Services.AddScoped<PaymentSettlement>();
builder.Services.AddScoped<WebhookProcessor>();
builder.Services.AddScoped<PendingResolver>();
builder.Services.AddScoped<PayoutService>();
if (!builder.Configuration.GetValue<bool>("OpenApi:Export"))
{
    builder.Services.AddHostedService<ReconciliationWorker>();
    builder.Services.AddHostedService<BudgetAlertWorker>();
}
builder.Services.AddKjJwtAuthentication();
builder.Services.AddKjIdempotency(builder.Configuration);

var app = builder.Build();

if (!(app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("OpenApi:Export")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
    // Tests swap in the Sqlite provider, which has no migrations to replay; Postgres keeps its migration history.
    if (db.Database.IsNpgsql())
    {
        db.Database.Migrate();
    }
    else
    {
        db.Database.EnsureCreated();
    }

    await LedgerSeeder.EnsureChartAsync(db, scope.ServiceProvider.GetRequiredService<TimeProvider>());
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Finance Service API")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    }).AllowAnonymous();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseKjIdempotency();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "FinanceService" }))
   .WithName("FinanceHealth")
   .WithSummary("Finance service health check")
   .WithTags("System")
   .AllowAnonymous();

app.MapPublicDonations();
app.MapAdminDonations();
app.MapOffline();
app.MapReceipts();
app.MapDonors();
app.MapCampaigns();
app.MapExpenses();
app.MapBudgets();
app.MapBanking();
app.MapLedger();
app.MapWebhooks();
app.MapReconciliation();
app.MapAuditLog<FinanceDbContext>("/api/v1/admin/finance/audit", "Finance");

app.Run();

namespace FinanceService
{
    public sealed class FinanceServiceTestEntryPoint;
}
