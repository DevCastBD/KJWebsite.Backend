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
                db.Database.GetAppliedMigrations().Should().HaveCount(8);
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

            // Erasure's one allowed edit: actor email, IP and browser may be nulled, and only that.
            await Exec(connectionString, "UPDATE membership.audit_events SET \"ActorEmail\" = 'who@example.org', \"Ip\" = '1.2.3.4' WHERE FALSE");
            await Refused(connectionString, "UPDATE membership.audit_events SET \"ActorEmail\" = 'who@example.org'");
            await Refused(connectionString, "UPDATE membership.audit_events SET \"ActorEmail\" = NULL, \"Action\" = 'x'");
            await Exec(connectionString, "UPDATE membership.audit_events SET \"ActorEmail\" = NULL, \"Ip\" = NULL, \"UserAgent\" = NULL");
            await Refused(connectionString, "DELETE FROM membership.audit_events");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TestPostgres.DropDatabaseAsync(databaseName);
        }
    }

    [RequiresPostgresFact]
    public async Task TheDirectoryQueryRunsOnPostgresAndKeepsUnapprovedFieldsOut()
    {
        var databaseName = await TestPostgres.CreateUniqueDatabaseAsync("membership_dir_test");
        var connectionString = TestPostgres.ConnectionStringFor(databaseName);
        try
        {
            using var factory = new PostgresMembershipApiFactory(connectionString);
            using var client = factory.CreateClient();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
                db.Members.AddRange(
                    new MembershipService.Data.Entities.MemberEntity { Id = "mem_a", UserId = "u1", MemberCode = "KJ-2026-0001", ApplicationId = "a1", FirstName = "Anika", LastName = "Rahman", NameBn = "আনিকা", Expertise = "Robotics", ConsentName = true, JoinedAt = DateTimeOffset.UtcNow },
                    new MembershipService.Data.Entities.MemberEntity { Id = "mem_b", UserId = "u2", MemberCode = "KJ-2026-0002", ApplicationId = "a2", FirstName = "Hidden", LastName = "Person", Expertise = "Robotics", ConsentExpertise = true, JoinedAt = DateTimeOffset.UtcNow },
                    new MembershipService.Data.Entities.MemberEntity { Id = "mem_c", UserId = "u3", MemberCode = "KJ-2026-0003", ApplicationId = "a3", FirstName = "Nobody", LastName = "Consented", JoinedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }

            var page = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/members/directory");
            page.GetProperty("total").GetInt32().Should().Be(2);
            var byName = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/members/directory?q=ANIKA");
            byName.GetProperty("items").GetArrayLength().Should().Be(1);
            (await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/members/directory?q=hidden")).GetProperty("total").GetInt32().Should().Be(0);
            (await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/members/directory?q=%E0%A6%86%E0%A6%A8%E0%A6%BF%E0%A6%95%E0%A6%BE")).GetProperty("total").GetInt32().Should().Be(1);

            // Committee terms use date-only columns and a join with the consent filter: check both on the real provider.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                db.Committees.Add(new MembershipService.Data.Entities.CommitteeEntity { Id = "com_x", Slug = "exec", Kind = "executive", NameEn = "Executive", NameBn = "নির্বাহী", CreatedAt = DateTimeOffset.UtcNow });
                db.CommitteeMembers.AddRange(
                    new MembershipService.Data.Entities.CommitteeMemberEntity { Id = "t1", CommitteeId = "com_x", MemberId = "mem_a", PositionKey = "chair", PositionEn = "Chair", PositionBn = "সভাপতি", StartsOn = today.AddDays(-5), CreatedAt = DateTimeOffset.UtcNow },
                    new MembershipService.Data.Entities.CommitteeMemberEntity { Id = "t2", CommitteeId = "com_x", MemberId = "mem_b", PositionKey = "member", PositionEn = "Member", PositionBn = "সদস্য", StartsOn = today.AddDays(-5), CreatedAt = DateTimeOffset.UtcNow },
                    new MembershipService.Data.Entities.CommitteeMemberEntity { Id = "t3", CommitteeId = "com_x", MemberId = "mem_a", PositionKey = "treasurer", PositionEn = "Treasurer", PositionBn = "কোষাধ্যক্ষ", StartsOn = today.AddYears(-2), EndsOn = today.AddDays(-1), CreatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }

            var chart = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/committees/exec");
            chart.GetProperty("current").GetArrayLength().Should().Be(1, "mem_b never agreed to be named");
            chart.GetProperty("past").GetArrayLength().Should().Be(1);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TestPostgres.DropDatabaseAsync(databaseName);
        }
    }

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
}
