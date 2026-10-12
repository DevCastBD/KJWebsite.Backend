using AuthIdentityService.Data;
using CtaSubmissionService.Data;
using CtaSubmissionService.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KJWebsite.IntegrationTests;

[Collection(nameof(PostgreSqlFixtures))]
public sealed class PostgreSqlMigrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AuthMigrationCreatesOnlyAuthSchemaTables()
    {
        using var factory = new AuthApiFactory(fixture);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        db.Database.GetAppliedMigrations().Should().ContainSingle().Which.Should().Contain("InitialPostgreSql");
        (await TableExists(fixture.AuthConnectionString, "auth", "users")).Should().BeTrue();
        (await TableExists(fixture.AuthConnectionString, "auth", "refresh_tokens")).Should().BeTrue();
        (await TableExists(fixture.AuthConnectionString, "auth", "__EFMigrationsHistory")).Should().BeTrue();
        (await TableExists(fixture.AuthConnectionString, "public", "users")).Should().BeFalse();
    }

    [Fact]
    public async Task CtaMigrationSeedsBengaliTextAndUsesBengaliCollation()
    {
        using var factory = new CtaApiFactory(fixture);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CtaDbContext>();

        db.Database.GetAppliedMigrations().Should().ContainSingle().Which.Should().Contain("InitialPostgreSql");
        (await TableExists(fixture.CtaConnectionString, "cta", "cta_submissions")).Should().BeTrue();
        (await TableExists(fixture.CtaConnectionString, "cta", "__EFMigrationsHistory")).Should().BeTrue();
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
