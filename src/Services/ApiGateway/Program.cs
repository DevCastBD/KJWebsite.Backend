using Scalar.AspNetCore;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddReverseProxy().LoadFromMemory(
[
    new RouteConfig { RouteId = "content", ClusterId = "content", Match = new RouteMatch { Path = "/api/v1/content/{**catch-all}" } },
    new RouteConfig { RouteId = "cta", ClusterId = "cta", Match = new RouteMatch { Path = "/api/v1/cta/{**catch-all}" } },
    new RouteConfig { RouteId = "auth", ClusterId = "auth", Match = new RouteMatch { Path = "/api/v1/auth/{**catch-all}" } },
    new RouteConfig { RouteId = "admin-content", ClusterId = "content", Match = new RouteMatch { Path = "/api/v1/admin/content/{**catch-all}" } }
],
[
    new ClusterConfig
    {
        ClusterId = "content",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["content"] = new() { Address = "http://localhost:7001/" }
        }
    },
    new ClusterConfig
    {
        ClusterId = "cta",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["cta"] = new() { Address = "http://localhost:7002/" }
        }
    },
    new ClusterConfig
    {
        ClusterId = "auth",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["auth"] = new() { Address = "http://localhost:7003/" }
        }
    }
]);

var app = builder.Build();

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
