using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Idempotency;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SponsorshipService.Data;
using SponsorshipService.Students;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "Sponsorship Service API";
        document.Info.Description = "Student sponsorship back office: student profiles, guardian consent, private photos and signed forms, and the donor preview. Children's data lives here, apart from the books.";
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

builder.Services.AddDbContext<SponsorshipDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("SponsorshipDb")
        ?? (configuration.GetValue<bool>("OpenApi:Export") ? "Host=localhost;Database=sponsorship" : throw new InvalidOperationException("ConnectionStrings:SponsorshipDb is required."));
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "sponsorship"));
    options.AddInterceptors(services.GetRequiredService<AuditInterceptor>());
});
builder.Services.AddKjAuditing("sponsorship", audit => audit
    // Personal data stays out of the log: the encrypted columns, the stories and the file keys are left out of before and after.
    .Audit<StudentEntity>(
        nameof(StudentEntity.NameProtected), nameof(StudentEntity.DateOfBirthProtected), nameof(StudentEntity.AddressProtected),
        nameof(StudentEntity.GuardianNameProtected), nameof(StudentEntity.GuardianPhoneProtected), nameof(StudentEntity.StoryEn),
        nameof(StudentEntity.StoryBn), nameof(StudentEntity.PhotoKey), nameof(StudentEntity.FormKey), nameof(StudentEntity.FormFileName))
    .Audit<ConsentEntity>(nameof(ConsentEntity.FormKey), nameof(ConsentEntity.Note)));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<SponsorshipOptions>(builder.Configuration.GetSection(SponsorshipOptions.SectionName));
// Persist the key ring (DataProtection:KeysPath) in any deployment: rows encrypted by one instance must open on the next.
var dataProtection = builder.Services.AddDataProtection(o => o.ApplicationDiscriminator = "KJWebsite.Sponsorship");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddSingleton<IStudentProtector, DataProtectionStudentProtector>();
builder.Services.AddSingleton<IStudentFileStore, LocalDiskStudentFileStore>();
builder.Services.AddHttpClient(ProjectCatalogue.ClientName, (services, client) =>
{
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Content:Url"] ?? "http://localhost:7001/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<IProjectCatalogue, ProjectCatalogue>();
builder.Services.AddSingleton<ProgrammeNames>();
builder.Services.AddKjJwtAuthentication();
builder.Services.AddKjIdempotency(builder.Configuration);

var app = builder.Build();

if (!(app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("OpenApi:Export")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>();
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
        options.WithTitle("Sponsorship Service API")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    }).AllowAnonymous();
}

// Everything here is about a child: no shared cache may keep a response, a refusal included, so the headers are set as it starts.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments(StudentEndpoints.Route))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Task.CompletedTask;
        });
    }

    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseKjIdempotency();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "SponsorshipService" }))
   .WithName("SponsorshipHealth")
   .WithSummary("Sponsorship service health check")
   .WithTags("System")
   .AllowAnonymous();

app.MapStudents();
app.MapStudentFiles();
app.MapAuditLog<SponsorshipDbContext>($"{StudentEndpoints.Route}/audit", "Sponsorship");

app.Run();

namespace SponsorshipService
{
    public sealed class SponsorshipServiceTestEntryPoint;
}
