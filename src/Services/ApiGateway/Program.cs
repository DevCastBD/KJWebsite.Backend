using ApiGateway;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Scalar.AspNetCore;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
var contentServiceUrl = builder.Configuration["Services:Content:Url"] ?? "http://localhost:7001/";
var ctaServiceUrl = builder.Configuration["Services:Cta:Url"] ?? "http://localhost:7002/";
var authServiceUrl = builder.Configuration["Services:Auth:Url"] ?? "http://localhost:7003/";
var membershipServiceUrl = builder.Configuration["Services:Membership:Url"] ?? "http://localhost:7004/";

builder.Services.Configure<ApiGateway.CorsOptions>(builder.Configuration.GetSection(ApiGateway.CorsOptions.SectionName));
builder.Services.AddCors();
builder.Services.AddGatewayRateLimiting(builder.Configuration);
builder.Services.AddSingleton<ICorsPolicyProvider, OriginCorsPolicyProvider>();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "KJWebsite API Gateway";
        document.Info.Description = "Entry point — routes /api/v1/content/*, /api/v1/cta/*, /api/v1/membership/*, /api/v1/auth/* to their respective downstream services.";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});
builder.Services.AddReverseProxy().LoadFromMemory(
[
    new RouteConfig { RouteId = "content", ClusterId = "content", Match = new RouteMatch { Path = "/api/v1/content/{**catch-all}" } },
    new RouteConfig { RouteId = "cta", ClusterId = "cta", Match = new RouteMatch { Path = "/api/v1/cta/{**catch-all}" } },
    new RouteConfig { RouteId = "auth", ClusterId = "auth", Match = new RouteMatch { Path = "/api/v1/auth/{**catch-all}" } },
    new RouteConfig { RouteId = "member-directory", ClusterId = "membership", Match = new RouteMatch { Path = "/api/v1/members/directory/{**catch-all}" } },
    new RouteConfig { RouteId = "member-sessions", ClusterId = "auth", Match = new RouteMatch { Path = "/api/v1/members/me/sessions/{**catch-all}" } },
    new RouteConfig { RouteId = "member-self", ClusterId = "membership", Match = new RouteMatch { Path = "/api/v1/members/me/{**catch-all}" } },
    new RouteConfig { RouteId = "admin-membership", ClusterId = "membership", Match = new RouteMatch { Path = "/api/v1/admin/membership/{**catch-all}" } },
    new RouteConfig { RouteId = "membership", ClusterId = "membership", Match = new RouteMatch { Path = "/api/v1/membership/{**catch-all}" } },
    new RouteConfig { RouteId = "admin-content", ClusterId = "content", Match = new RouteMatch { Path = "/api/v1/admin/content/{**catch-all}" } },
    new RouteConfig { RouteId = "sitemap", ClusterId = "content", Match = new RouteMatch { Path = "/sitemap.xml" } },
    new RouteConfig { RouteId = "feed-rss", ClusterId = "content", Match = new RouteMatch { Path = "/feed.rss" } },
    new RouteConfig { RouteId = "feed-atom", ClusterId = "content", Match = new RouteMatch { Path = "/feed.atom" } },
    new RouteConfig { RouteId = "admin-auth", ClusterId = "auth", Match = new RouteMatch { Path = "/api/v1/admin/auth/{**catch-all}" } }
],
[
    new ClusterConfig
    {
        ClusterId = "content",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["content"] = new() { Address = contentServiceUrl }
        }
    },
    new ClusterConfig
    {
        ClusterId = "cta",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["cta"] = new() { Address = ctaServiceUrl }
        }
    },
    new ClusterConfig
    {
        ClusterId = "auth",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["auth"] = new() { Address = authServiceUrl }
        }
    },
    new ClusterConfig
    {
        ClusterId = "membership",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["membership"] = new() { Address = membershipServiceUrl }
        }
    }
]);

var app = builder.Build();

// Resolve eagerly so an invalid origin in configuration fails startup, not the first request.
_ = app.Services.GetRequiredService<ICorsPolicyProvider>();

app.UseForwardedHeaders();
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
