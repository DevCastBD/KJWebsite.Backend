using CtaSubmissionService.Contracts;
using CtaSubmissionService.Data;
using CtaSubmissionService.Data.Entities;
using CtaSubmissionService.Validation;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using KJWebsite.BuildingBlocks.Validation;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKjValidation<CtaSubmissionRequestValidator>();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "CTA Submission Service API";
        document.Info.Description = "Accepts and persists call-to-action form submissions (registration, contact, get-involved).";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

builder.Services.AddDbContext<CtaDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("CtaDb")
        ?? (configuration.GetValue<bool>("OpenApi:Export") ? "Host=localhost;Database=cta" : throw new InvalidOperationException("ConnectionStrings:CtaDb is required."));
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "cta"));
});

builder.Services.AddKjJwtAuthentication();

var app = builder.Build();

if (!(app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("OpenApi:Export")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CtaDbContext>();
    // Tests swap in the Sqlite provider, which has no migrations to replay; Postgres keeps its migration history.
    if (db.Database.IsNpgsql())
    {
        db.Database.Migrate();
    }
    else
    {
        db.Database.EnsureCreated();
    }

    if (app.Environment.IsDevelopment() && !db.Submissions.Any(s => s.Id == "subm_dev_bengali"))
    {
        db.Submissions.Add(new CtaSubmissionEntity
        {
            Id = "subm_dev_bengali",
            FormName = "contact",
            CtaType = "contact",
            Language = "bn",
            SourcePath = "/contact",
            ValuesJson = "{\"firstName\":\"বাংলা\"}",
            FirstName = "বাংলা",
            SubmittedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("CTA Submission Service API")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    }).AllowAnonymous();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "CtaSubmissionService" }))
   .WithName("CtaHealth")
   .WithSummary("CTA service health check")
   .WithTags("System")
.AllowAnonymous();

app.MapPost("/api/v1/cta/submissions", async (HttpContext context, CtaSubmissionRequest payload, CtaDbContext db) =>
{
    var valuesJson = System.Text.Json.JsonSerializer.Serialize(payload.Values);
    var id = $"subm_{Guid.NewGuid():N}";
    var createdAt = DateTimeOffset.UtcNow;
    var awaitingProfile = LegacyAwaitingUserMapper.TryMap(payload.Values);

    db.Submissions.Add(new CtaSubmissionEntity
    {
        Id = id,
        FormName = payload.FormName,
        CtaType = payload.CtaType,
        Language = payload.Language,
        SourcePath = payload.SourcePath,
        ValuesJson = valuesJson,
        SubmittedAt = payload.SubmittedAt.ToUniversalTime(),
        CreatedAt = createdAt,
        FirstName = awaitingProfile?.FirstName,
        LastName = awaitingProfile?.LastName,
        Gender = awaitingProfile?.Gender,
        ReasonForJoining = awaitingProfile?.ReasonForJoining,
        PresentOrganization = awaitingProfile?.PresentOrganization,
        VolunteeingExperience = awaitingProfile?.VolunteeingExperience,
        DateOfBirth = awaitingProfile?.DateOfBirth,
        CityOfResidence = awaitingProfile?.CityOfResidence,
        CountryOfResidence = awaitingProfile?.CountryOfResidence
    });

    await db.SaveChangesAsync();

    return Results.Created($"/api/v1/cta/submissions/{id}", new { id, status = "accepted", created_at = createdAt });
})
.WithName("CreateCtaSubmission")
.WithSummary("Submit a CTA form")
.WithDescription("Accepts a call-to-action form submission. Valid formName values: registration, contact, footer-contact, project-involved. Valid ctaType values: join, donate, partnership, get-involved, contact.")
.WithTags("CTA")
.WithValidation<CtaSubmissionRequest>()
.AllowAnonymous();

app.Run();

namespace CtaSubmissionService
{
    public sealed class CtaSubmissionServiceTestEntryPoint;
}
