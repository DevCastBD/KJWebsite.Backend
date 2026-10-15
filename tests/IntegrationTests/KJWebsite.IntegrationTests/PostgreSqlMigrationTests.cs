using AuthIdentityService.Data;
using CtaSubmissionService.Data;
using CtaSubmissionService.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KJWebsite.IntegrationTests;

public sealed class PostgreSqlMigrationTests
{
    [RequiresPostgresFact]
    public async Task AuthMigrationCreatesOnlyAuthSchemaTables()
    {
        TestPostgres.EnsureDatabaseExists("auth_test");
        var connectionString = TestPostgres.ConnectionStringFor("auth_test");
        using var factory = new PostgresAuthApiFactory(connectionString);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        db.Database.GetAppliedMigrations().Should().ContainSingle().Which.Should().Contain("InitialPostgreSql");
        (await TableExists(connectionString, "auth", "users")).Should().BeTrue();
        (await TableExists(connectionString, "auth", "refresh_tokens")).Should().BeTrue();
        (await TableExists(connectionString, "auth", "__EFMigrationsHistory")).Should().BeTrue();
        (await TableExists(connectionString, "public", "users")).Should().BeFalse();
    }

    [RequiresPostgresFact]
    public async Task CtaMigrationSeedsBengaliTextAndUsesBengaliCollation()
    {
        TestPostgres.EnsureDatabaseExists("cta_test");
        var connectionString = TestPostgres.ConnectionStringFor("cta_test");
        using var factory = new PostgresCtaApiFactory(connectionString);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CtaDbContext>();

        db.Database.GetAppliedMigrations().Should().ContainSingle().Which.Should().Contain("InitialPostgreSql");
        (await TableExists(connectionString, "cta", "cta_submissions")).Should().BeTrue();
        (await TableExists(connectionString, "cta", "__EFMigrationsHistory")).Should().BeTrue();
        var seed = await db.Submissions.SingleAsync(s => s.Id == "subm_dev_bengali");
        seed.ValuesJson.Should().Contain("বাংলা");

        var prefix = $"collation_{Guid.NewGuid():N}_";
        var names = new[] { "ই", "আ", "অ" };
        db.Submissions.AddRange(names.Select((name, index) => new CtaSubmissionEntity
        {
            Id = prefix + index,
            FormName = "contact",
            CtaType = "contact",
            Language = "bn",
            SourcePath = "/contact",
            ValuesJson = "{}",
            FirstName = name,
            SubmittedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        }));
        await db.SaveChangesAsync();

        var ordered = await db.Submissions.Where(s => s.Id.StartsWith(prefix))
            .OrderBy(s => s.FirstName).Select(s => s.FirstName).ToListAsync();
        ordered.Should().Equal("অ", "আ", "ই");
    }

    private static async Task<bool> TableExists(string connectionString, string schema, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table)", connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
