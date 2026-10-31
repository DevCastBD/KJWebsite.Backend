using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceService.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KJWebsite.IntegrationTests;

public sealed class FinancePostgresTests
{
    [RequiresPostgresFact]
    public async Task MigrationsApplyAndTheDatabaseKeepsTheLedgerAppendOnlyAndSigned()
    {
        var databaseName = await TestPostgres.CreateUniqueDatabaseAsync("finance_test");
        var connectionString = TestPostgres.ConnectionStringFor(databaseName);
        try
        {
            using var factory = new PostgresFinanceApiFactory(connectionString);
            using var client = factory.CreateClient();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
                db.Database.GetPendingMigrations().Should().BeEmpty();
                db.Database.GetAppliedMigrations().Should().HaveCount(2);
            }

            using var created = await client.PostAsJsonAsync("/api/v1/donations", new
            {
                amount = "1250.50",
                currency = "USD",
                frequency = "monthly",
                designation = new { type = "campaign", id = "flood-relief" },
                donor = new { name = "বাংলা Giver", email = "pg@example.org" },
                anonymous = true,
                coverFees = true,
                language = "bn"
            });
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("total").GetString().Should().Be("1301.25"); // ceil(1250.50 / 0.961 = 1301.2487)
            (await client.GetAsync($"/api/v1/donations/{body.GetProperty("reference").GetString()}/status")).StatusCode.Should().Be(HttpStatusCode.OK);

            // The status/list/totals queries translate on PostgreSQL too, not just on Sqlite.
            var viewer = TestTokens.Issue([KJWebsite.BuildingBlocks.Security.Permissions.FinanceView]);
            foreach (var path in new[] { "donations?status=pending&from=2000-01-01&designation=campaign:flood-relief", "donations/totals", "recurring-plans" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/admin/finance/{path}");
                request.Headers.Authorization = new("Bearer", viewer);
                using var response = await client.SendAsync(request);
                response.StatusCode.Should().Be(HttpStatusCode.OK, path + await response.Content.ReadAsStringAsync());
            }

            await Refused(connectionString, "UPDATE finance.donations SET \"Amount\" = 1");
            await Refused(connectionString, "DELETE FROM finance.donations");
            await Refused(connectionString, "UPDATE finance.donation_events SET \"Status\" = 'confirmed'");
            await Refused(connectionString, "DELETE FROM finance.donation_events");
            await Refused(connectionString, "UPDATE finance.recurring_plans SET \"Amount\" = 1");
            await Refused(connectionString, "DELETE FROM finance.recurring_plans");
            await Exec(connectionString, "INSERT INTO finance.payment_attempts (\"DonationId\", \"Gateway\", \"Status\", \"At\") SELECT \"Id\", 'sslcommerz', 'created', now() FROM finance.donations LIMIT 1");
            await Refused(connectionString, "UPDATE finance.payment_attempts SET \"Status\" = 'created'");
            await Refused(connectionString, "DELETE FROM finance.payment_attempts");

            // Money is exact: the column holds cents and nothing finer.
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT \"Amount\"::text, \"FeeAmount\"::text FROM finance.donations", connection);
                await using var reader = await command.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                (reader.GetString(0), reader.GetString(1)).Should().Be(("1250.50", "50.75"));
            }

            // The sign rule: gifts are positive, reversals negative.
            await CheckViolation(connectionString, InsertDonation("don_bad1", "KJD-BAD0000001", "one_time", "-5.00", "0.00"));
            await CheckViolation(connectionString, InsertDonation("don_bad2", "KJD-BAD0000002", "one_time", "0.00", "0.00"));
            await CheckViolation(connectionString, InsertDonation("don_bad3", "KJD-BAD0000003", "reversal", "5.00", "0.00"));
            await Exec(connectionString, InsertDonation("don_ok1", "KJD-OK00000001", "reversal", "-5.00", "0.00"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TestPostgres.DropDatabaseAsync(databaseName);
        }
    }

    private static string InsertDonation(string id, string reference, string kind, string amount, string fee) =>
        $"INSERT INTO finance.donations (\"Id\", \"Reference\", \"Kind\", \"Amount\", \"FeeAmount\", \"Currency\", \"DesignationType\", \"IsAnonymous\", \"DonorNameProtected\", \"DonorEmailProtected\", \"Language\", \"CreatedAt\", \"ReceivedOn\") " +
        $"VALUES ('{id}', '{reference}', '{kind}', {amount}, {fee}, 'BDT', 'general', false, 'x', 'y', 'en', now(), current_date)";

    private static async Task Exec(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Refused(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var failure = await command.Invoking(c => c.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be("42501");
    }

    private static async Task CheckViolation(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var failure = await command.Invoking(c => c.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be("23514");
    }
}
