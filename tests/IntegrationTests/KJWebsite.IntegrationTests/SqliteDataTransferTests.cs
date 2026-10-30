using CtaSubmissionService.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SqliteToPostgres;

namespace KJWebsite.IntegrationTests;

public sealed class SqliteDataTransferTests
{
    [Fact]
    public async Task RefusesToImportAuthBecausePasswordsWerePlainText()
    {
        var import = () => SqliteDataTransfer.CopyAsync("unused.db", "Host=unused", "auth", ["users", "refresh_tokens"]);

        await import.Should().ThrowAsync<NotSupportedException>().WithMessage("*plain text*");
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
}
