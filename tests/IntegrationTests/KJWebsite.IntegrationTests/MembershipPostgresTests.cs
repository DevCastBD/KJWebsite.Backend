using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KJWebsite.IntegrationTests;

public sealed class MembershipPostgresTests
{
    [RequiresPostgresFact]
    public async Task MigrationsApplyAndTheDatabaseKeepsHistoryAndAuditAppendOnly()
    {
        var databaseName = await TestPostgres.CreateUniqueDatabaseAsync("membership_test");
        var connectionString = TestPostgres.ConnectionStringFor(databaseName);
        try
        {
            using var factory = new PostgresMembershipApiFactory(connectionString);
            using var client = factory.CreateClient();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
                db.Database.GetPendingMigrations().Should().BeEmpty();
                db.Database.GetAppliedMigrations().Should().HaveCount(4);
            }

            using var created = await client.PostAsJsonAsync("/api/v1/membership/applications", new
            {
                firstName = "বাংলা",
                lastName = "Rahman",
                email = "pg@example.org",
                phone = "01710000000",
                bloodGroup = "O+",
                currentAddress = "Dhaka",
                academicInstitute = "DU",
                occupation = "Student",
                language = "bn"
            });
            created.StatusCode.Should().Be(HttpStatusCode.Created);

            using var transition = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/membership/applications/transition");
            transition.Headers.Authorization = new("Bearer", TestTokens.Issue([Permissions.MembershipManage]));
            var id = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString();
            transition.Content = JsonContent.Create(new { ids = new[] { id }, to = "under_review" });
            (await client.SendAsync(transition)).StatusCode.Should().Be(HttpStatusCode.OK);

            await Refused(connectionString, "UPDATE membership.application_status_events SET \"Status\" = 'approved'");
            await Refused(connectionString, "UPDATE membership.audit_events SET \"Action\" = 'x'");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TestPostgres.DropDatabaseAsync(databaseName);
        }
    }

    private static async Task Refused(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var failure = await command.Invoking(c => c.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be("42501");
    }
}
