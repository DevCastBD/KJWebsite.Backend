using AuthIdentityService.Data;
using CtaSubmissionService.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SqliteToPostgres;

namespace KJWebsite.IntegrationTests;

public sealed class SqliteDataTransferTests
{
    [RequiresPostgresFact]
    public async Task CopiesAuthUsersAndRefreshTokensWithUtcTimestamps()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"kj-auth-transfer-{Guid.NewGuid():N}.db");
        var databaseName = await TestPostgres.CreateUniqueDatabaseAsync("auth_transfer_test");
        var connectionString = TestPostgres.ConnectionStringFor(databaseName);

        try
        {
            await CreateAuthSqliteFixture(sourcePath);
            var options = new DbContextOptionsBuilder<AuthDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "auth"))
                .Options;
            await using (var db = new AuthDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            await SqliteDataTransfer.CopyAsync(sourcePath, connectionString, "auth", ["users", "refresh_tokens"]);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT u."FirstName", u."IsMailingAddressSameAsPermanentAddress", t."ExpiresAt"
                FROM auth.users u JOIN auth.refresh_tokens t ON t."UserId" = u."Id"
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("বাংলা");
            reader.GetBoolean(1).Should().BeTrue();
            reader.GetDateTime(2).Should().Be(new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc));
            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            await TestPostgres.DropDatabaseAsync(databaseName);
        }
    }

    [RequiresPostgresFact]
    public async Task CopiesBengaliDevelopmentDataAndRefusesANonEmptyTarget()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"kj-cta-transfer-{Guid.NewGuid():N}.db");
        var databaseName = await TestPostgres.CreateUniqueDatabaseAsync("cta_transfer_test");
        var connectionString = TestPostgres.ConnectionStringFor(databaseName);

        try
        {
            await CreateSqliteFixture(sourcePath);
            var options = new DbContextOptionsBuilder<CtaDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "cta"))
                .Options;
            await using (var db = new CtaDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            await SqliteDataTransfer.CopyAsync(sourcePath, connectionString, "cta", ["cta_submissions"]);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT \"FirstName\", \"ValuesJson\" FROM cta.cta_submissions", connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("বাংলা");
            reader.GetString(1).Should().Contain("বাংলা");
            (await reader.ReadAsync()).Should().BeFalse();
            await reader.CloseAsync();

            var retry = () => SqliteDataTransfer.CopyAsync(sourcePath, connectionString, "cta", ["cta_submissions"]);
            await retry.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            await TestPostgres.DropDatabaseAsync(databaseName);
        }
    }

    private static async Task CreateSqliteFixture(string path)
    {
        await using var source = new SqliteConnection($"Data Source={path}");
        await source.OpenAsync();
        await using var command = source.CreateCommand();
        command.CommandText = """
            CREATE TABLE cta_submissions (
                Id TEXT NOT NULL, FormName TEXT NOT NULL, CtaType TEXT NOT NULL,
                Language TEXT NOT NULL, SourcePath TEXT NOT NULL, ValuesJson TEXT NOT NULL,
                SubmittedAt TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                FirstName TEXT, LastName TEXT, Gender TEXT, ReasonForJoining TEXT,
                PresentOrganization TEXT, VolunteeingExperience TEXT, DateOfBirth TEXT,
                CityOfResidence TEXT, CountryOfResidence TEXT
            );
            INSERT INTO cta_submissions VALUES (
                'subm_old', 'contact', 'contact', 'bn', '/contact', '{"firstName":"বাংলা"}',
                '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00',
                'বাংলা', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateAuthSqliteFixture(string path)
    {
        await using var source = new SqliteConnection($"Data Source={path}");
        await source.OpenAsync();
        await using var command = source.CreateCommand();
        command.CommandText = """
            CREATE TABLE users (
                Id TEXT NOT NULL, Email TEXT NOT NULL, Password TEXT NOT NULL,
                Role TEXT NOT NULL, Status TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                FirstName TEXT, LastName TEXT, Gender TEXT, ReasonForJoining TEXT,
                PresentOrganization TEXT, VolunteeingExperience TEXT, DateOfBirth TEXT,
                CityOfResidence TEXT, CountryOfResidence TEXT, PermanentAddress TEXT,
                MailingAddress TEXT, IsMailingAddressSameAsPermanentAddress INTEGER NOT NULL,
                BloodGroup TEXT, AreasOfExpertise TEXT, HighestDegree TEXT,
                DisabilitiesIfAny TEXT, Nationality TEXT, PersonalWebPage TEXT,
                SocialMediaLink TEXT
            );
            CREATE TABLE refresh_tokens (
                Id TEXT NOT NULL, Token TEXT NOT NULL, UserId TEXT NOT NULL,
                CreatedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL, RevokedAt TEXT
            );
            INSERT INTO users (Id, Email, Password, Role, Status, CreatedAt, FirstName,
                IsMailingAddressSameAsPermanentAddress) VALUES
                ('usr_old', 'old@example.test', 'dev-only', 'admin', 'active',
                '2026-01-01T06:00:00+06:00', 'বাংলা', 1);
            INSERT INTO refresh_tokens VALUES
                ('ref_old', 'dev-token', 'usr_old', '2026-01-01T06:00:00+06:00',
                '2026-01-31T06:00:00+06:00', NULL);
            """;
        await command.ExecuteNonQueryAsync();
    }
}
