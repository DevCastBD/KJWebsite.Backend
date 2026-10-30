using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KJWebsite.IntegrationTests;

public sealed class ContentPostgresTests
{
    [RequiresPostgresFact]
    public async Task ContentMigrationCreatesTheSchemaSeedsBengaliAndSurvivesARestart()
    {
        var databaseName = await TestPostgres.CreateUniqueDatabaseAsync("content_test");
        var connectionString = TestPostgres.ConnectionStringFor(databaseName);
        try
        {
            using (var first = new PostgresContentApiFactory(connectionString))
            using (var client = first.CreateClient())
            {
                using var scope = first.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
                db.Database.GetAppliedMigrations().Should().ContainSingle().Which.Should().Contain("InitialContent");

                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/content/projects") { Content = JsonContent.Create(new { slug = "pg-survivor", status = "active", translations = new[] { new { lang = "bn", title = "পিজি" } } }) };
                request.Headers.Authorization = new("Bearer", TestTokens.ContentAdmin());
                (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Created);
            }

            using var second = new PostgresContentApiFactory(connectionString);
            using var restarted = second.CreateClient();
            var survivor = await restarted.GetFromJsonAsync<JsonElement>("/api/v1/content/projects/pg-survivor?lang=bn");
            survivor.GetProperty("title").GetString().Should().Be("পিজি");
            var seeded = await restarted.GetFromJsonAsync<JsonElement>("/api/v1/content/projects/pathshala?lang=bn");
            seeded.GetProperty("title").GetString().Should().Be("পাঠশালা");

            (await Scalar(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema='content' AND table_name IN ('content_items','content_translations','projects','news_articles')")).Should().Be(4);
            (await Scalar(connectionString, "SELECT count(*) FROM content.content_items WHERE \"Type\"='project' AND \"Slug\"='pathshala'")).Should().Be(1);
            (await Scalar(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_schema='content' AND table_name='content_translations' AND column_name='Title' AND collation_name='bn-x-icu'")).Should().Be(1);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await TestPostgres.DropDatabaseAsync(databaseName);
        }
    }

    private static async Task<long> Scalar(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
