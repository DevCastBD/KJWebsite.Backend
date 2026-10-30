using ApiGateway;
using KJWebsite.BuildingBlocks;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var health = builder.AddKjObservability("ApiGateway");
builder.Services.Configure<ApiGateway.CorsOptions>(builder.Configuration.GetSection(ApiGateway.CorsOptions.SectionName));
builder.Services.AddCors();
builder.Services.AddGatewayRateLimiting(builder.Configuration);
builder.Services.AddSingleton<ICorsPolicyProvider, OriginCorsPolicyProvider>();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "KJWebsite API Gateway";
        document.Info.Description = "Entry point — routes /api/v1/content/*, /api/v1/cta/*, /api/v1/auth/* to their respective downstream services.";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddGatewayCompression();
builder.Services.AddHttpClient(DownstreamHealthCheck.ClientName);
health.AddCheck<DownstreamHealthCheck>("downstream", tags: [KjObservability.ReadyTag]);

var app = builder.Build();
app.UseKjObservability();

GatewayConfigurationGuard.EnsureDestinations(app.Environment, app.Configuration);

// Resolve eagerly so an invalid origin in configuration fails startup, not the first request.
_ = app.Services.GetRequiredService<ICorsPolicyProvider>();

app.UseForwardedHeaders();
app.UseGatewayCompression();
app.UseSecurityHeaders();
app.UseCors();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("KJWebsite API Gateway")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });
}

app.MapKjObservability();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ApiGateway" }))
   .WithName("GatewayHealth")
   .WithSummary("Gateway health check")
   .WithTags("System");
app.MapReverseProxy();

app.Run();

namespace ApiGateway
{
    public sealed class ApiGatewayTestEntryPoint;
}
