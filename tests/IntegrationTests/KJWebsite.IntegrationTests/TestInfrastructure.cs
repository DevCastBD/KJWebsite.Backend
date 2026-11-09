using AuthIdentityService.Data;
using ContentService.Data;
using CtaSubmissionService.Data;
using FinanceService.Data;
using MembershipService.Data;
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
            services.AddDbContext<TContext>((provider, options) =>
            {
                options.UseSqlite($"Data Source={_dbPath}");
                ConfigureDbContext(options, provider);
                // The service's own registration was removed above; keep its audit interceptor so tests exercise the real write path.
                if (provider.GetService<KJWebsite.BuildingBlocks.Auditing.AuditInterceptor>() is { } audit)
                {
                    options.AddInterceptors(audit);
                }
            });
        });
    }

    /// <summary>Lets a test add its own interceptors or options to the service's DbContext.</summary>
    protected virtual void ConfigureDbContext(DbContextOptionsBuilder options, IServiceProvider provider)
    {
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // Only this factory's own pool: ClearAllPools() would close the connections of every other test running in parallel.
        using (var own = new SqliteConnection($"Data Source={_dbPath}"))
        {
            SqliteConnection.ClearPool(own);
        }

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

    public static string Issue(string[] permissions, string? signingPem = null, TimeSpan? lifetime = null, string subject = "usr_test", IEnumerable<System.Security.Claims.Claim>? extraClaims = null)
    {
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(signingPem ?? TestJwtKeys.Primary);
        var claims = new List<System.Security.Claims.Claim> { new("sub", subject), new("sid", "ses_test") };
        claims.AddRange(permissions.Select(p => new System.Security.Claims.Claim("permissions", p)));
        claims.AddRange(extraClaims ?? []);
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

    /// <summary>A token holding every content permission, publishing included.</summary>
    public static string ContentAdmin() => Issue(
        [KJWebsite.BuildingBlocks.Security.Permissions.ContentView, KJWebsite.BuildingBlocks.Security.Permissions.ContentCreate,
         KJWebsite.BuildingBlocks.Security.Permissions.ContentUpdate, KJWebsite.BuildingBlocks.Security.Permissions.ContentDelete,
         KJWebsite.BuildingBlocks.Security.Permissions.ContentPublish]);

    /// <summary>An editor: can create and edit but not publish (the role ContentEditor holds).</summary>
    public static string ContentEditor() => Issue(
        [KJWebsite.BuildingBlocks.Security.Permissions.ContentView, KJWebsite.BuildingBlocks.Security.Permissions.ContentCreate,
         KJWebsite.BuildingBlocks.Security.Permissions.ContentUpdate]);
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

/// <summary>Membership with a throwaway attachment folder and a log sink the tests can search.</summary>
public sealed class MembershipApiFactory : SqliteWebApplicationFactory<MembershipService.MembershipServiceTestEntryPoint, MembershipDbContext>
{
    public string AttachmentsPath { get; } = Path.Combine(Path.GetTempPath(), $"kj-test-attachments-{Guid.NewGuid():N}");

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Where the service's calls to Identity go; set before the first client is created. Default: a real network call.</summary>
    public HttpMessageHandler? AuthHandler { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
            services.AddHttpClient("auth").ConfigurePrimaryHttpMessageHandler(() => AuthHandler ?? new HttpClientHandler()));
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Membership:AttachmentsPath"] = AttachmentsPath,
                ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary)
            }));
        builder.ConfigureServices(services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(AttachmentsPath))
        {
            Directory.Delete(AttachmentsPath, recursive: true);
        }
    }
}

public sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Sink(_lines);

    public void Dispose()
    {
    }

    private sealed class Sink(System.Collections.Concurrent.ConcurrentQueue<string> lines) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception) + exception);
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

public sealed class ContentApiFactory : SqliteWebApplicationFactory<ContentService.ContentServiceTestEntryPoint, ContentDbContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary) }));
    }
}

public sealed class PostgresMembershipApiFactory(string connectionString)
    : PostgresWebApplicationFactory<MembershipService.MembershipServiceTestEntryPoint>(connectionString, "MembershipDb")
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary) }));
    }
}

public sealed class PostgresContentApiFactory(string connectionString)
    : PostgresWebApplicationFactory<ContentService.ContentServiceTestEntryPoint>(connectionString, "ContentDb")
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary) }));
    }
}

public sealed class GatewayApiFactory : WebApplicationFactory<ApiGateway.ApiGatewayTestEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
}

public class FinanceApiFactory : SqliteWebApplicationFactory<FinanceService.FinanceServiceTestEntryPoint, FinanceDbContext>
{
    /// <summary>Extra configuration for this factory's service (gateway switches and credentials); set before the first client is created.</summary>
    public Dictionary<string, string?> Settings { get; } = [];

    /// <summary>Stands in for every payment gateway's HTTP endpoint; with none set, calls would go to the real network, so tests that enable a gateway set one.</summary>
    public HttpMessageHandler? GatewayHandler { get; set; }

    /// <summary>Lets a test register extra services (for example another payment gateway) before the host is built.</summary>
    public Action<IServiceCollection>? ConfigureTestServices { get; set; }

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Where Finance's calls to Identity go; set before the first client is created. Default: a real network call.</summary>
    public HttpMessageHandler? AuthHandler { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary) });
            configuration.AddInMemoryCollection(Settings);
        });
        builder.ConfigureServices(services =>
        {
            foreach (var name in new[] { "gateway-sslcommerz", "gateway-bkash", "gateway-nagad", "gateway-stripe", "gateway-paypal" })
            {
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => GatewayHandler ?? new HttpClientHandler());
            }

            services.AddHttpClient("auth").ConfigurePrimaryHttpMessageHandler(() => AuthHandler ?? new HttpClientHandler());
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs);
            ConfigureTestServices?.Invoke(services);
        });
    }
}

public sealed class PostgresFinanceApiFactory(string connectionString)
    : PostgresWebApplicationFactory<FinanceService.FinanceServiceTestEntryPoint>(connectionString, "FinanceDb")
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary) }));
    }
}

public static class TestCampaigns
{
    /// <summary>Creates an active campaign directly, so a test can designate gifts to it.</summary>
    public static async Task SeedAsync(IServiceProvider services, string slug, string status = "active", string currency = "BDT", decimal goal = 500_000m, DateOnly? endsOn = null, DateOnly? startsOn = null)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        db.Campaigns.Add(new FinanceService.Data.Entities.CampaignEntity
        {
            Id = "cmp_" + Guid.NewGuid().ToString("N"),
            Slug = slug,
            Title = slug,
            GoalAmount = goal,
            Currency = currency,
            Status = status,
            StartsOn = startsOn,
            EndsOn = endsOn,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Sensitive finance actions (manual adjustments, posting and donation reversals) are requests that a second person approves. Most tests only need the effect, so this sends
/// the request as the caller, approves it as a different, fixed approver, and returns what the old direct endpoint returned. Tests of the control itself send requests directly.
/// </summary>
public static class TestControls
{
    public static readonly string Approver = TestTokens.Issue([KJWebsite.BuildingBlocks.Security.Permissions.FinanceView, KJWebsite.BuildingBlocks.Security.Permissions.FinanceControlApprove], subject: "usr_approver");

    public static bool IsControlled(HttpRequestMessage request)
    {
        var path = request.RequestUri!.OriginalString.Split('?')[0];
        return request.Method == HttpMethod.Post && (path.EndsWith("/ledger/postings", StringComparison.Ordinal)
            || (path.Contains("/ledger/postings/", StringComparison.Ordinal) && path.EndsWith("/reversal", StringComparison.Ordinal))
            || (path.Contains("/finance/donations/", StringComparison.Ordinal) && path.EndsWith("/reversal", StringComparison.Ordinal)));
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        var controlled = IsControlled(request);
        var response = await client.SendAsync(request);
        if (!controlled || response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            return response;
        }

        var asked = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<System.Text.Json.JsonElement>(response.Content);
        using var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/finance/controls/requests/{asked.GetProperty("id").GetString()}/approve")
        {
            Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Approver) },
            Content = System.Net.Http.Json.JsonContent.Create(new { })
        };
        var approved = await client.SendAsync(approve);
        if (!approved.IsSuccessStatusCode)
        {
            return approved;
        }

        var decided = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<System.Text.Json.JsonElement>(approved.Content);
        var path = decided.GetProperty("postingId").ValueKind == System.Text.Json.JsonValueKind.String
            ? $"/api/v1/admin/finance/ledger/postings/{decided.GetProperty("postingId").GetString()}"
            : $"/api/v1/admin/finance/donations/{decided.GetProperty("donationId").GetString()}";
        using var read = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Approver) } };
        var view = await client.SendAsync(read);
        return new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = view.Content };
    }
}
