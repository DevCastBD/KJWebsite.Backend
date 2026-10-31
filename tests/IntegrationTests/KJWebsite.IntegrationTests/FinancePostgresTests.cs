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
                db.Database.GetAppliedMigrations().Should().HaveCount(7);
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
            await ReconciliationRecordsAreAppendOnlyAsync(connectionString);

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

    [RequiresPostgresFact]
    public async Task ReceiptNumbersStayGaplessAndChainedWhenManyDonationsAreConfirmedAtOnce()
    {
        var databaseName = await TestPostgres.CreateUniqueDatabaseAsync("finance_receipts_test");
        var connectionString = TestPostgres.ConnectionStringFor(databaseName);
        try
        {
            using var factory = new PostgresFinanceApiFactory(connectionString);
            using var client = factory.CreateClient();
            var ids = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                using var created = await client.PostAsJsonAsync("/api/v1/donations", new
                {
                    amount = "100.00",
                    currency = "BDT",
                    frequency = "once",
                    designation = new { type = "general" },
                    donor = new { name = $"Donor {i}", email = $"d{i}@example.org" },
                    anonymous = false,
                    coverFees = false,
                    language = "en"
                });
                ids.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
            }

            var results = await Task.WhenAll(ids.Select(async id =>
            {
                using var scope = factory.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<FinanceService.Ledger.DonationConfirmation>().ConfirmAsync(id, "bkash", null);
            }));

            results.Should().OnlyContain(r => r.IsSuccess && r.Value, "every donation is confirmed exactly once, whatever the race");
            using var db = factory.Services.CreateScope();
            var context = db.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var receipts = (await context.Receipts.AsNoTracking().ToListAsync()).OrderBy(r => r.Seq).ToList();
            receipts.Select(r => r.Seq).Should().Equal(Enumerable.Range(1, 20).Select(n => (long)n));
            receipts.Select(r => r.DonationId).Should().BeEquivalentTo(ids);
            (await context.Postings.CountAsync()).Should().Be(20);
            var check = await db.ServiceProvider.GetRequiredService<FinanceService.Receipts.ReceiptService>().VerifyAsync(receipts[0].Series, default);
            check.Valid.Should().BeTrue(string.Join("; ", check.Problems));
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
                // Cleanup must not hide a real failure from the test body.
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

    private static async Task ReconciliationRecordsAreAppendOnlyAsync(string connectionString)
    {
        await Exec(connectionString, "INSERT INTO finance.payment_events (\"Gateway\", \"EventKey\", \"Outcome\", \"At\") VALUES ('sslcommerz', 'k1', 'confirmed', now())");
        await Exec(connectionString, "INSERT INTO finance.webhook_rejections (\"Gateway\", \"ReasonCode\", \"SourceIp\", \"At\") VALUES ('bkash', 'BAD_SIGNATURE', '1.2.3.4', now())");
        await Exec(connectionString, "INSERT INTO finance.payouts (\"Gateway\", \"PayoutRef\", \"PaidOn\", \"ImportedAt\") VALUES ('sslcommerz', 'P1', current_date, now())");
        await Exec(connectionString, "INSERT INTO finance.payout_lines (\"PayoutId\", \"Reference\", \"Gross\", \"Fee\", \"Net\", \"Currency\", \"MatchStatus\") SELECT \"Id\", 'KJD-AAAAAAAAAA', 10, 1, 9, 'BDT', 'unmatched' FROM finance.payouts LIMIT 1");
        foreach (var table in new[] { "payment_events", "webhook_rejections", "payouts", "payout_lines" })
        {
            await Refused(connectionString, $"DELETE FROM finance.{table}");
        }

        await Refused(connectionString, "UPDATE finance.payment_events SET \"Outcome\" = 'failed'");
        await Refused(connectionString, "UPDATE finance.webhook_rejections SET \"ReasonCode\" = 'X'");
        await Refused(connectionString, "UPDATE finance.payouts SET \"PayoutRef\" = 'P2'");
        await Refused(connectionString, "UPDATE finance.payout_lines SET \"Gross\" = 99");

        await Exec(connectionString, "INSERT INTO finance.payment_settlements (\"DonationId\", \"Gateway\", \"TransactionId\", \"SettledAmount\", \"SettledCurrency\", \"Fee\", \"ExchangeRate\", \"At\") VALUES ('don_a', 'stripe', 'pi_1', 19.50, 'GBP', 0.87, 0.78123456, now())");
        await Exec(connectionString, "INSERT INTO finance.provider_notices (\"Gateway\", \"EventKey\", \"Kind\", \"DonationId\", \"At\") VALUES ('stripe', 'evt_1', 'refund', 'don_a', now())");
        foreach (var table in new[] { "payment_settlements", "provider_notices" })
        {
            await Refused(connectionString, $"DELETE FROM finance.{table}");
        }

        await Refused(connectionString, "UPDATE finance.payment_settlements SET \"SettledAmount\" = 1");
        await Refused(connectionString, "UPDATE finance.provider_notices SET \"Kind\" = 'dispute'");
        await using (var settleTwice = new NpgsqlConnection(connectionString))
        {
            await settleTwice.OpenAsync();
            await using var again = new NpgsqlCommand("INSERT INTO finance.payment_settlements (\"DonationId\", \"Gateway\", \"SettledAmount\", \"SettledCurrency\", \"At\") VALUES ('don_a', 'stripe', 1, 'GBP', now())", settleTwice);
            (await again.Invoking(c => c.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        }

        await Exec(connectionString, "INSERT INTO finance.offline_records (\"Id\", \"DonationId\", \"Method\", \"ReceivedOn\", \"RecordedBy\", \"RecordedAt\") VALUES ('off_1', 'don_b', 'bank', current_date, 'usr_maker', now())");
        await Exec(connectionString, "INSERT INTO finance.offline_decisions (\"OfflineRecordId\", \"Decision\", \"DecidedBy\", \"At\") VALUES ('off_1', 'approved', 'usr_checker', now())");
        await Exec(connectionString, "INSERT INTO finance.offline_proofs (\"Id\", \"OfflineRecordId\", \"FileName\", \"ContentType\", \"SizeBytes\", \"Sha256\", \"StorageKey\", \"UploadedBy\", \"UploadedAt\", \"RetainUntil\") VALUES ('prf_1', 'off_1', 'a.pdf', 'application/pdf', 10, 'ab', 'k', 'usr_maker', now(), current_date)");
        foreach (var table in new[] { "offline_records", "offline_decisions", "offline_proofs" })
        {
            await Refused(connectionString, $"DELETE FROM finance.{table}");
        }

        await Refused(connectionString, "UPDATE finance.offline_records SET \"Note\" = 'x'");
        await Refused(connectionString, "UPDATE finance.offline_decisions SET \"Decision\" = 'rejected'");
        await Refused(connectionString, "UPDATE finance.offline_proofs SET \"FileName\" = 'b.pdf'");
        await using (var decideTwice = new NpgsqlConnection(connectionString))
        {
            await decideTwice.OpenAsync();
            await using var again = new NpgsqlCommand("INSERT INTO finance.offline_decisions (\"OfflineRecordId\", \"Decision\", \"DecidedBy\", \"At\") VALUES ('off_1', 'rejected', 'usr_other', now())", decideTwice);
            (await again.Invoking(c => c.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        }

        await Exec(connectionString, "INSERT INTO finance.receipts (\"Id\", \"DonationId\", \"Series\", \"Seq\", \"Number\", \"PrevHash\", \"Hash\", \"IssuedAt\", \"ReceivedOn\", \"DonorNameProtected\", \"DonorEmailProtected\", \"DonorKey\", \"Amount\", \"FeeAmount\", \"Currency\", \"DesignationType\", \"IsAnonymous\", \"Language\", \"IsInKind\", \"OrganisationJson\") VALUES ('rcp_x', 'don_r', '2099-00', 1, 'KJR-2099-00-000001', 'p', 'h', now(), current_date, 'n', 'e', 'k', 10, 0, 'BDT', 'general', false, 'en', false, '{}')");
        await Exec(connectionString, "INSERT INTO finance.receipt_voids (\"ReceiptId\", \"Reason\", \"At\") VALUES ('rcp_x', 'test', now())");
        await Exec(connectionString, "INSERT INTO finance.receipt_reissues (\"ReceiptId\", \"Reason\", \"ReissuedBy\", \"At\") VALUES ('rcp_x', 'test', 'u', now())");
        await Exec(connectionString, "INSERT INTO finance.statement_sends (\"DonorKey\", \"FiscalYear\", \"QueuedBy\", \"At\") VALUES ('k', '2099-00', 'u', now())");
        foreach (var table in new[] { "receipts", "receipt_voids", "receipt_reissues", "statement_sends" })
        {
            await Refused(connectionString, $"DELETE FROM finance.{table}");
        }

        await Refused(connectionString, "UPDATE finance.receipts SET \"Amount\" = 1");
        await Refused(connectionString, "UPDATE finance.receipt_voids SET \"Reason\" = 'x'");
        await Refused(connectionString, "UPDATE finance.statement_sends SET \"QueuedBy\" = 'x'");
        foreach (var uniqueViolation in new[]
        {
            "INSERT INTO finance.receipts (\"Id\", \"DonationId\", \"Series\", \"Seq\", \"Number\", \"PrevHash\", \"Hash\", \"IssuedAt\", \"ReceivedOn\", \"DonorNameProtected\", \"DonorEmailProtected\", \"DonorKey\", \"Amount\", \"FeeAmount\", \"Currency\", \"DesignationType\", \"IsAnonymous\", \"Language\", \"IsInKind\", \"OrganisationJson\") VALUES ('rcp_y', 'don_other', '2099-00', 1, 'KJR-OTHER', 'p', 'h', now(), current_date, 'n', 'e', 'k', 10, 0, 'BDT', 'general', false, 'en', false, '{}')",
            "INSERT INTO finance.statement_sends (\"DonorKey\", \"FiscalYear\", \"QueuedBy\", \"At\") VALUES ('k', '2099-00', 'u', now())"
        })
        {
            await using var twice = new NpgsqlConnection(connectionString);
            await twice.OpenAsync();
            await using var again = new NpgsqlCommand(uniqueViolation, twice);
            (await again.Invoking(c => c.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        }

        // The replay guard and the settle-once rule are unique indexes.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var duplicate = new NpgsqlCommand("INSERT INTO finance.payment_events (\"Gateway\", \"EventKey\", \"Outcome\", \"At\") VALUES ('sslcommerz', 'k1', 'confirmed', now())", connection);
        (await duplicate.Invoking(c => c.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
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
