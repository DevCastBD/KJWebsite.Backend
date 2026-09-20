using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace KJWebsite.IntegrationTests;

[CollectionDefinition(nameof(PostgreSqlFixtures))]
public sealed class PostgreSqlFixtures : ICollectionFixture<PostgreSqlFixture>;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _authDatabase = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("auth")
        .WithUsername("testuser")
        .WithPassword("testpassword")
        .Build();

    private readonly PostgreSqlContainer _ctaDatabase = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cta")
        .WithUsername("testuser")
        .WithPassword("testpassword")
        .Build();

    public string AuthConnectionString => _authDatabase.GetConnectionString();
    public string CtaConnectionString => _ctaDatabase.GetConnectionString();

    public Task InitializeAsync() => Task.WhenAll(_authDatabase.StartAsync(), _ctaDatabase.StartAsync());

    public Task DisposeAsync() => Task.WhenAll(_authDatabase.DisposeAsync().AsTask(), _ctaDatabase.DisposeAsync().AsTask());
}

public abstract class PostgreSqlWebApplicationFactory<TEntryPoint>(string connectionString, string connectionName)
    : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:" + connectionName] = connectionString,
                ["Database:Provider"] = "PostgreSql",
                ["Database:EnsureCreated"] = "true"
            });
        });
    }
}

public sealed class AuthApiFactory(PostgreSqlFixture fixture)
    : PostgreSqlWebApplicationFactory<AuthIdentityService.AuthIdentityServiceTestEntryPoint>(fixture.AuthConnectionString, "AuthDb");

public sealed class CtaApiFactory(PostgreSqlFixture fixture)
    : PostgreSqlWebApplicationFactory<CtaSubmissionService.CtaSubmissionServiceTestEntryPoint>(fixture.CtaConnectionString, "CtaDb");

public sealed class ContentApiFactory : WebApplicationFactory<ContentService.ContentServiceTestEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
}

public sealed class GatewayApiFactory : WebApplicationFactory<ApiGateway.ApiGatewayTestEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
}
