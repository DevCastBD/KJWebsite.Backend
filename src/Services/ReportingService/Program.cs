using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using ReportingService.Aggregates;
using ReportingService.Dashboards;
using ReportingService.Data;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "Reporting Service API";
        document.Info.Description = "Reporting and transparency: nightly aggregate tables with as-of dates, dashboards, impact metrics and reports. Every figure comes from the warehouse, never from another service's tables.";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

builder.Services.AddDbContext<ReportingDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("ReportingDb")
        ?? (configuration.GetValue<bool>("OpenApi:Export") ? "Host=localhost;Database=reporting" : throw new InvalidOperationException("ConnectionStrings:ReportingDb is required."));
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "reporting"));
    options.AddInterceptors(services.GetRequiredService<AuditInterceptor>());
});
builder.Services.AddKjAuditing("reporting", audit => audit.Audit<ReportingService.Data.Entities.AggregateRunEntity>());
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<AggregateOptions>(builder.Configuration.GetSection(AggregateOptions.SectionName));
builder.Services.AddHttpClient(AggregateRefresher.ClientName, (services, client) =>
    client.Timeout = TimeSpan.FromSeconds(services.GetRequiredService<IConfiguration>().GetValue("Reporting:Aggregates:FeedTimeoutSeconds", 30)));
builder.Services.AddScoped<AggregateRefresher>();
builder.Services.AddScoped<AggregateQuery>();
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection(DashboardOptions.SectionName));
builder.Services.AddScoped<DashboardService>();
if (!builder.Configuration.GetValue<bool>("OpenApi:Export"))
{
    builder.Services.AddHostedService<AggregateWorker>();
}

builder.Services.AddKjJwtAuthentication();

var app = builder.Build();

if (!(app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("OpenApi:Export")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
    // Tests swap in the Sqlite provider, which has no migrations to replay; Postgres keeps its migration history.
    if (db.Database.IsNpgsql())
    {
        db.Database.Migrate();
    }
    else
    {
        db.Database.EnsureCreated();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Reporting Service API")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    }).AllowAnonymous();
}

app.UsePrivateResponses("/api/v1/admin/reporting");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ReportingService" }))
   .WithName("ReportingHealth")
   .WithSummary("Reporting service health check")
   .WithTags("System")
   .AllowAnonymous();

app.MapAggregates();
app.MapDashboards();
app.MapAuditLog<ReportingDbContext>("/api/v1/admin/reporting/audit", "Reporting");

app.Run();

namespace ReportingService
{
    public sealed class ReportingServiceTestEntryPoint;
}
