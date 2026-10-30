using AuthIdentityService.Data;
using CtaSubmissionService.Data;
using CtaSubmissionService.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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

        db.Database.GetAppliedMigrations().Should().HaveCount(2).And.Contain(m => m.EndsWith("InitialPostgreSql") || m.EndsWith("AdoptAspNetIdentity"));
        (await TableExists(connectionString, "auth", "users")).Should().BeTrue();
        (await TableExists(connectionString, "auth", "member_profiles")).Should().BeTrue();
        (await TableExists(connectionString, "auth", "roles")).Should().BeTrue();
        (await TableExists(connectionString, "auth", "refresh_tokens")).Should().BeTrue();
        (await TableExists(connectionString, "auth", "__EFMigrationsHistory")).Should().BeTrue();
        (await TableExists(connectionString, "public", "users")).Should().BeFalse();
    }

    [RequiresPostgresFact]
    public async Task IdentityMigrationDropsPlainTextPasswordsAndKeepsProfilesAndRoles()
    {
        var databaseName = await TestPostgres.CreateUniqueDatabaseAsync("auth_upgrade_test");
        var connectionString = TestPostgres.ConnectionStringFor(databaseName);
        try
        {
            var options = new DbContextOptionsBuilder<AuthDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "auth"))
                .Options;
            await using var db = new AuthDbContext(options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261012143134_InitialPostgreSql");
            await Execute(connectionString, """
                INSERT INTO auth.users ("Id","Email","Password","Role","Status","CreatedAt","FirstName","VolunteeingExperience","IsMailingAddressSameAsPermanentAddress")
                VALUES ('usr_a','a@example.test','plain-text-secret','admin','active',now(),'বাংলা','5 years',true),
                       ('usr_b','b@example.test','another-secret','editor','active',now(),'Bee',NULL,false);
                """);

            await migrator.MigrateAsync();

            (await Scalar(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_schema='auth' AND table_name='users' AND column_name IN ('Password','Role','FirstName')")).Should().Be(0);
            (await Scalar(connectionString, "SELECT count(*) FROM auth.users WHERE \"PasswordHash\" IS NOT NULL")).Should().Be(0);
            (await Scalar(connectionString, "SELECT count(*) FROM auth.users WHERE \"PasswordHash\" ILIKE '%secret%' OR \"SecurityStamp\" ILIKE '%secret%' OR \"PhoneNumber\" IS NOT NULL")).Should().Be(0);
            (await Scalar(connectionString, "SELECT count(*) FROM auth.member_profiles WHERE \"UserId\"='usr_a' AND \"FirstName\"='বাংলা' AND \"VolunteeingExperience\"='5 years' AND \"IsMailingAddressSameAsPermanentAddress\"")).Should().Be(1);
            (await Scalar(connectionString, "SELECT count(*) FROM auth.user_roles ur JOIN auth.roles r ON r.\"Id\"=ur.\"RoleId\" WHERE (ur.\"UserId\"='usr_a' AND r.\"Name\"='Admin') OR (ur.\"UserId\"='usr_b' AND r.\"Name\"='Member')")).Should().Be(2);
        }
        finally
        {
            await TestPostgres.DropDatabaseAsync(databaseName);
        }
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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
