using AuthIdentityService.Data;
using CtaSubmissionService.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KJWebsite.IntegrationTests;

/// <summary>
/// SQLite-by-default: plain CRUD smoke tests get a throwaway database with no external setup.
/// Overrides the DbContextOptions the service's own Program.cs registered for Npgsql.
/// </summary>
public abstract class SqliteWebApplicationFactory<TEntryPoint, TContext> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
    where TContext : DbContext
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kj-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<TContext>>();
            services.AddDbContext<TContext>(options => options.UseSqlite($"Data Source={_dbPath}"));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        TryDeleteFile(_dbPath);
    }

    // Windows can briefly hold the file handle after ClearAllPools(); a short retry avoids leaking temp files.
    private static void TryDeleteFile(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }
    }
}

public sealed class AuthApiFactory : SqliteWebApplicationFactory<AuthIdentityService.AuthIdentityServiceTestEntryPoint, AuthDbContext>;

public sealed class CtaApiFactory : SqliteWebApplicationFactory<CtaSubmissionService.CtaSubmissionServiceTestEntryPoint, CtaDbContext>;

/// <summary>
/// Postgres-required: tests that verify real PostgreSQL behavior (migrations, schemas, collation).
/// Connects to the database named by <paramref name="connectionName"/> under KJ_TEST_POSTGRES.
/// </summary>
public abstract class PostgresWebApplicationFactory<TEntryPoint>(string connectionString, string connectionName)
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
                ["ConnectionStrings:" + connectionName] = connectionString
            });
        });
    }
}

public sealed class PostgresAuthApiFactory(string connectionString)
    : PostgresWebApplicationFactory<AuthIdentityService.AuthIdentityServiceTestEntryPoint>(connectionString, "AuthDb");

public sealed class PostgresCtaApiFactory(string connectionString)
    : PostgresWebApplicationFactory<CtaSubmissionService.CtaSubmissionServiceTestEntryPoint>(connectionString, "CtaDb");

public sealed class ContentApiFactory : WebApplicationFactory<ContentService.ContentServiceTestEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
}

public sealed class GatewayApiFactory : WebApplicationFactory<ApiGateway.ApiGatewayTestEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
}
