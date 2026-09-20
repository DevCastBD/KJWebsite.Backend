using CtaSubmissionService.Contracts;
using CtaSubmissionService.Data;
using CtaSubmissionService.Data.Entities;
using KJWebsite.BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
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

var usePostgreSql = string.Equals(builder.Configuration["Database:Provider"], "PostgreSql", StringComparison.OrdinalIgnoreCase);
var ctaConnectionString = builder.Configuration.GetConnectionString("CtaDb") ?? "Data Source=cta.db";
builder.Services.AddDbContext<CtaDbContext>(options =>
{
    if (usePostgreSql)
    {
        options.UseNpgsql(ctaConnectionString);
        return;
    }

    options.UseSqlite(ctaConnectionString);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CtaDbContext>();
    if (usePostgreSql && builder.Configuration.GetValue<bool>("Database:EnsureCreated"))
    {
        db.Database.EnsureCreated();
    }
    else
    {
        db.Database.Migrate();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("CTA Submission Service API")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });
}

var allowedForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "registration", "contact", "footer-contact", "project-involved" };
var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "join", "donate", "partnership", "get-involved", "contact" };
var allowedLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bn", "en" };

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "CtaSubmissionService" }))
   .WithName("CtaHealth")
   .WithSummary("CTA service health check")
   .WithTags("System");

app.MapPost("/api/v1/cta/submissions", async (HttpContext context, CtaSubmissionRequest payload, CtaDbContext db) =>
{
    if (!allowedForms.Contains(payload.FormName) || !allowedTypes.Contains(payload.CtaType) || !allowedLangs.Contains(payload.Language))
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid formName, ctaType, or language.");
    }

    var valuesJson = System.Text.Json.JsonSerializer.Serialize(payload.Values);
    if (valuesJson.Length > 32_000)
    {
        return ApiError.Create(context, StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", "values payload exceeds 32KB.");
    }

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
        SubmittedAt = payload.SubmittedAt,
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
.WithTags("CTA");

app.Run();

namespace CtaSubmissionService
{
    public sealed class CtaSubmissionServiceTestEntryPoint;
}
