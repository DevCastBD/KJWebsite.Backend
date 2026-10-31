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
                db.Database.GetAppliedMigrations().Should().HaveCount(3);
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

            await LedgerRulesHoldAsync(factory, connectionString);

            // The sign rule: gifts are positive, reversals negative.
            await CheckViolation(connectionString, InsertDonation("don_bad1", "KJD-BAD0000001", "one_time", "-5.00", "0.00"));
            await CheckViolation(connectionString, InsertDonation("don_bad2", "KJD-BAD0000002", "one_time", "0.00", "0.00"));
            await CheckViolation(connectionString, InsertDonation("don_bad3", "KJD-BAD0000003", "reversal", "5.00", "0.00"));
            await Exec(connectionString, InsertDonation("don_ok1", "KJD-OK00000001", "reversal", "-5.00", "0.00"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            try
            {
                await TestPostgres.DropDatabaseAsync(databaseName);
            }
            catch (PostgresException)
            {
                // Cleanup must not hide a real failure from the test body; a leftover throwaway database is harmless.
            }
        }
    }

    private static async Task LedgerRulesHoldAsync(PostgresFinanceApiFactory factory, string connectionString)
    {
        // The chart was seeded at startup. Post one real balanced posting through the service.
        long postingId;
        using (var scope = factory.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<FinanceService.Ledger.LedgerService>();
            var posted = await ledger.PostAsync(new FinanceService.Ledger.PostingDraft(ledger.Today, "pg check", "adjustment", null,
                [FinanceService.Ledger.LedgerLine.Debit("1010", KJWebsite.BuildingBlocks.Finance.Money.Of(10m, "BDT")), FinanceService.Ledger.LedgerLine.Credit("4900", KJWebsite.BuildingBlocks.Finance.Money.Of(10m, "BDT"))], "pg"), "tester");
            posted.IsSuccess.Should().BeTrue();
            postingId = posted.Value!.Posting.Id;
        }

        await Refused(connectionString, "UPDATE finance.postings SET \"Memo\" = 'x'");
        await Refused(connectionString, "DELETE FROM finance.postings");
        await Refused(connectionString, "UPDATE finance.ledger_entries SET \"Debit\" = 1 WHERE \"Debit\" > 0");
        await Refused(connectionString, "DELETE FROM finance.ledger_entries");
        await Refused(connectionString, "DELETE FROM finance.accounts WHERE \"Code\" = '1010'");
        await Refused(connectionString, "UPDATE finance.accounts SET \"Code\" = '9999' WHERE \"Code\" = '1010'");
        await Refused(connectionString, "UPDATE finance.accounts SET \"Type\" = 'expense' WHERE \"Code\" = '1010'");
        await Exec(connectionString, "UPDATE finance.accounts SET \"Name\" = 'Cash in hand' WHERE \"Code\" = '1010'");

        // One side per line, never both and never neither.
        await CheckViolation(connectionString, $"INSERT INTO finance.ledger_entries (\"PostingId\", \"AccountId\", \"Debit\", \"Credit\", \"Currency\") SELECT {postingId}, \"Id\", 5, 5, 'BDT' FROM finance.accounts LIMIT 1");
        await CheckViolation(connectionString, $"INSERT INTO finance.ledger_entries (\"PostingId\", \"AccountId\", \"Debit\", \"Credit\", \"Currency\") SELECT {postingId}, \"Id\", 0, 0, 'BDT' FROM finance.accounts LIMIT 1");

        // A posting must have two or more lines that balance per currency, checked when the transaction commits.
        const string header = "INSERT INTO finance.postings (\"PostedOn\", \"PeriodKey\", \"FiscalYear\", \"Memo\", \"SourceType\", \"CreatedAt\") VALUES (current_date, '{0}', '2026-27', 'raw', 'adjustment', now()) RETURNING \"Id\"";
        const string line = "INSERT INTO finance.ledger_entries (\"PostingId\", \"AccountId\", \"Debit\", \"Credit\", \"Currency\") VALUES ({0}, (SELECT \"Id\" FROM finance.accounts WHERE \"Code\" = '{1}'), {2}, {3}, '{4}')";
        var period = DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        await CheckViolationInTransaction(connectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, header, period), []);
        await CheckViolationInTransaction(connectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, header, period), [string.Format(System.Globalization.CultureInfo.InvariantCulture, line, "{id}", "1010", 10, 0, "BDT")]);
        await CheckViolationInTransaction(connectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, header, period),
            [string.Format(System.Globalization.CultureInfo.InvariantCulture, line, "{id}", "1010", 10, 0, "BDT"), string.Format(System.Globalization.CultureInfo.InvariantCulture, line, "{id}", "4900", 0, 9, "BDT")]);
        await CheckViolationInTransaction(connectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, header, period),
            [string.Format(System.Globalization.CultureInfo.InvariantCulture, line, "{id}", "1010", 10, 0, "BDT"), string.Format(System.Globalization.CultureInfo.InvariantCulture, line, "{id}", "4900", 0, 10, "USD")]);
        await CommitInTransaction(connectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, header, period),
            [string.Format(System.Globalization.CultureInfo.InvariantCulture, line, "{id}", "1010", 10, 0, "BDT"), string.Format(System.Globalization.CultureInfo.InvariantCulture, line, "{id}", "4900", 0, 10, "BDT")]);

        // A closed period takes no postings (and the close itself cannot be undone).
        await Exec(connectionString, $"INSERT INTO finance.period_closes (\"PeriodKey\", \"ClosedAt\") VALUES ('{period}', now())");
        await CheckViolationInTransaction(connectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, header, period),
            [string.Format(System.Globalization.CultureInfo.InvariantCulture, line, "{id}", "1010", 10, 0, "BDT"), string.Format(System.Globalization.CultureInfo.InvariantCulture, line, "{id}", "4900", 0, 10, "BDT")]);
        await Refused(connectionString, "DELETE FROM finance.period_closes");
        await Refused(connectionString, "UPDATE finance.period_closes SET \"ClosedBy\" = 'x'");
    }

    private static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction, long Id)> BeginAsync(string connectionString, string header)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(header, connection, transaction);
        var id = (long)(await command.ExecuteScalarAsync())!;
        return (connection, transaction, id);
    }

    private static async Task CommitInTransaction(string connectionString, string header, string[] lines)
    {
        var (connection, transaction, id) = await BeginAsync(connectionString, header);
        await using (connection)
        await using (transaction)
        {
            foreach (var sql in lines)
            {
                await using var command = new NpgsqlCommand(sql.Replace("{id}", id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), connection, transaction);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
    }

    // The failure may come from a statement or, for the deferred balance rules, from the commit itself.
    private static async Task CheckViolationInTransaction(string connectionString, string header, string[] lines)
    {
        PostgresException? failure = null;
        try
        {
            await CommitInTransaction(connectionString, header, lines);
        }
        catch (PostgresException exception)
        {
            failure = exception;
        }

        failure.Should().NotBeNull("the database must refuse this posting");
        failure!.SqlState.Should().Be("23514");
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
