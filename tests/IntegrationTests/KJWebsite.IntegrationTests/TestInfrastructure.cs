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

/// <summary>
/// The auth service on its local-development default, SQLite, against a throwaway database file.
/// No provider key is set, so the service runs its real migrations — the path a contributor hits.
/// Needs no Docker, so it also runs on machines without it.
/// </summary>
public sealed class AuthSqliteApiFactory : WebApplicationFactory<AuthIdentityService.AuthIdentityServiceTestEntryPoint>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"kj-auth-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AuthDb"] = $"Data Source={_databasePath}"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}

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
