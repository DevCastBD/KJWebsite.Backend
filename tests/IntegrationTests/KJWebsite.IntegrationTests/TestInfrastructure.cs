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

/// <summary>Fixed RSA keys for tests, so no test writes a development key into the user's profile.</summary>
public static class TestJwtKeys
{
    public static readonly string Primary = Generate();
    public static readonly string Other = Generate();

    private static string Generate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}

/// <summary>Access tokens signed with the test key, for services that only verify (Content, CTA).</summary>
public static class TestTokens
{
    public static string PublicKeyPem(string privatePem)
    {
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(privatePem);
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    public static string Issue(string[] permissions, string? signingPem = null, TimeSpan? lifetime = null)
    {
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(signingPem ?? TestJwtKeys.Primary);
        var claims = new List<System.Security.Claims.Claim> { new("sub", "usr_test"), new("sid", "ses_test") };
        claims.AddRange(permissions.Select(p => new System.Security.Claims.Claim("permissions", p)));
        var now = DateTime.UtcNow;
        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "kjwebsite-auth",
            Audience = "kjwebsite-api",
            Subject = new System.Security.Claims.ClaimsIdentity(claims),
            NotBefore = now.AddMinutes(-1),
            Expires = now.Add(lifetime ?? TimeSpan.FromMinutes(15)),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa.ExportParameters(true)), Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256)
        });
    }

    /// <summary>A token holding every content permission.</summary>
    public static string ContentAdmin() => Issue(
        [KJWebsite.BuildingBlocks.Security.Permissions.ContentView, KJWebsite.BuildingBlocks.Security.Permissions.ContentCreate,
         KJWebsite.BuildingBlocks.Security.Permissions.ContentUpdate, KJWebsite.BuildingBlocks.Security.Permissions.ContentDelete]);
}

public sealed class AuthApiFactory : SqliteWebApplicationFactory<AuthIdentityService.AuthIdentityServiceTestEntryPoint, AuthDbContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:PrivateKeyPem"] = TestJwtKeys.Primary }));
    }
}

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
    : PostgresWebApplicationFactory<AuthIdentityService.AuthIdentityServiceTestEntryPoint>(connectionString, "AuthDb")
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:PrivateKeyPem"] = TestJwtKeys.Primary }));
    }
}

public sealed class PostgresCtaApiFactory(string connectionString)
    : PostgresWebApplicationFactory<CtaSubmissionService.CtaSubmissionServiceTestEntryPoint>(connectionString, "CtaDb");

public sealed class ContentApiFactory : WebApplicationFactory<ContentService.ContentServiceTestEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary) }));
    }
}

public sealed class GatewayApiFactory : WebApplicationFactory<ApiGateway.ApiGatewayTestEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
}
