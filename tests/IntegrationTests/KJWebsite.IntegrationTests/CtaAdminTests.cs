using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CtaSubmissionService.Admin;
using CtaSubmissionService.AntiSpam;
using CtaSubmissionService.Data;
using CtaSubmissionService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KJWebsite.IntegrationTests;

public sealed class CsvTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("+8801712345678", "+8801712345678")]
    [InlineData("+880 171-234 5678", "+880 171-234 5678")]
    [InlineData("বাংলা", "বাংলা")]
    [InlineData(null, "")]
    public void CellsAreQuotedAndFormulasNeutralised(string? input, string expected) => Csv.Escape(input).Should().Be(expected);

    [Fact]
    public void OutputStartsWithAByteOrderMarkAndUsesCrLf()
    {
        var bytes = Csv.Build(["a", "b"], [["1", "বাংলা"]]);

        bytes[..3].Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(bytes[3..]).Should().Be("a,b\r\n1,বাংলা\r\n");
    }
}

public sealed class CtaAdminTests : IDisposable
{
    private readonly CtaApiFactory _factory = new();
    private readonly HttpClient _client;

    public CtaAdminTests()
    {
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope(); // Development seeds one Bengali sample; start from an empty inbox
        scope.ServiceProvider.GetRequiredService<CtaDbContext>().Submissions.ExecuteDelete();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string[]? permissions, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (permissions is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(permissions));
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static readonly string[] View = [Permissions.CtaView];
    private static readonly string[] Manage = [Permissions.CtaView, Permissions.CtaManage];
    private static readonly string[] Export = [Permissions.CtaExport];

    private async Task<JsonElement> Get(string url, string[]? permissions = null)
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Get, url, permissions ?? View));
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task Seed(string id, string form = "contact", string language = "en", string? first = "Test", string? last = null,
        string status = "new", string? assignee = null, int ageDays = 0, string? values = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CtaDbContext>();
        var created = DateTimeOffset.UtcNow.AddDays(-ageDays);
        db.Submissions.Add(new CtaSubmissionEntity
        {
            Id = id,
            FormName = form,
            CtaType = form == "registration" ? "join" : "contact",
            Language = language,
            SourcePath = "/x",
            ValuesJson = values ?? JsonSerializer.Serialize(new { firstName = first, lastName = last, email = $"{id}@example.com", message = "Hello" }),
            FirstName = first,
            LastName = last,
            Status = status,
            AssigneeId = assignee,
            SubmittedAt = created,
            CreatedAt = created
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<SubmissionAuditEntity>> AuditRows()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CtaDbContext>().SubmissionAudit.AsNoTracking().OrderBy(a => a.At).ToListAsync();
    }

    [Theory]
    [InlineData("GET", "/api/v1/admin/cta/submissions")]
    [InlineData("GET", "/api/v1/admin/cta/submissions/subm_x")]
    [InlineData("GET", "/api/v1/admin/cta/submissions/export")]
    [InlineData("PATCH", "/api/v1/admin/cta/submissions/subm_x")]
    public async Task EveryAdminEndpointRejectsAnonymousCallers(string method, string url)
    {
        using var response = await _client.SendAsync(Request(new HttpMethod(method), url, null, method == "PATCH" ? new { status = "spam" } : null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EachActionNeedsItsOwnPermission()
    {
        await Seed("subm_a");
        var nothing = new[] { Permissions.ContentView };

        (await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/admin/cta/submissions", nothing))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Request(HttpMethod.Patch, "/api/v1/admin/cta/submissions/subm_a", View, new { status = "spam" }))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/admin/cta/submissions/export", View))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/admin/cta/submissions/export", Export))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SubmissionsAreListedNewestFirstAndPaged()
    {
        for (var i = 0; i < 5; i++)
        {
            await Seed($"subm_{i}", ageDays: i);
        }

        var page = await Get("/api/v1/admin/cta/submissions?limit=2&offset=1");

        page.GetProperty("total").GetInt32().Should().Be(5);
        page.GetProperty("limit").GetInt32().Should().Be(2);
        page.GetProperty("offset").GetInt32().Should().Be(1);
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Equal("subm_1", "subm_2");
        var first = page.GetProperty("items")[0];
        first.GetProperty("email").GetString().Should().Be("subm_1@example.com");
        first.GetProperty("status").GetString().Should().Be("new");
    }

    [Fact]
    public async Task ListFiltersCombine()
    {
        await Seed("subm_bn", language: "bn", first: "রহিম", last: "উদ্দিন", ageDays: 10);
        await Seed("subm_reg", form: "registration", assignee: "usr_7", status: "in_review", ageDays: 2);
        await Seed("subm_spam", status: "spam", first: "Bot", ageDays: 1);

        async Task<string[]> Ids(string query) =>
            [.. (await Get($"/api/v1/admin/cta/submissions?{query}")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];

        (await Ids("status=spam")).Should().Equal("subm_spam");
        (await Ids("formName=registration")).Should().Equal("subm_reg");
        (await Ids("language=bn")).Should().Equal("subm_bn");
        (await Ids("assigneeId=usr_7")).Should().Equal("subm_reg");
        (await Ids("q=" + Uri.EscapeDataString("রহিম"))).Should().Equal("subm_bn");
        (await Ids("q=BOT")).Should().Equal("subm_spam"); // case-insensitive
        (await Ids("q=100%25")).Should().BeEmpty("wildcard characters are literal");
        (await Ids($"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-3).ToString("O"))}")).Should().BeEquivalentTo("subm_reg", "subm_spam");
        (await Ids($"to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-5).ToString("O"))}")).Should().Equal("subm_bn");
        (await Ids("status=in_review&language=en")).Should().Equal("subm_reg");
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=101", "limit")]
    [InlineData("offset=-1", "offset")]
    [InlineData("status=bogus", "status")]
    public async Task BadListQueriesNameTheField(string query, string field)
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Get, $"/api/v1/admin/cta/submissions?{query}", View));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain($"\"{field}\"").And.Contain("urn:kjwebsite:error:validation-error");
    }

    [Fact]
    public async Task OneSubmissionCanBeReadAndUnknownOnesAre404()
    {
        await Seed("subm_one", values: "{\"firstName\":\"Rahim\",\"note\":\"বাংলা\"}");

        var detail = await Get("/api/v1/admin/cta/submissions/subm_one");

        detail.GetProperty("values").GetProperty("note").GetString().Should().Be("বাংলা");
        detail.GetProperty("status").GetString().Should().Be("new");
        using var missing = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/admin/cta/submissions/nope", View));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StatusAssigneeAndNotesCanBeUpdatedAndEveryChangeIsAudited()
    {
        await Seed("subm_p");

        using var response = await _client.SendAsync(Request(HttpMethod.Patch, "/api/v1/admin/cta/submissions/subm_p", Manage,
            new { status = "in_review", assigneeId = "usr_9", notes = "Called Rahim on 0171-SECRET" }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await Get("/api/v1/admin/cta/submissions/subm_p");
        detail.GetProperty("status").GetString().Should().Be("in_review");
        detail.GetProperty("assigneeId").GetString().Should().Be("usr_9");
        detail.GetProperty("notes").GetString().Should().Contain("Rahim");
        detail.GetProperty("history").GetArrayLength().Should().Be(1);

        var update = (await AuditRows()).Single(a => a.Action == "update");
        update.SubmissionId.Should().Be("subm_p");
        update.Actor.Should().Be("usr_test");
        update.Detail.Should().Contain("status: new -> in_review").And.Contain("assignee: none -> usr_9").And.Contain("notes changed");
        update.Detail.Should().NotContain("SECRET", "the audit trail records that notes changed, not what they say");
    }

    [Fact]
    public async Task EmptyStringsClearAndUnchangedPatchesLeaveNoAuditRow()
    {
        await Seed("subm_c", assignee: "usr_1");

        (await _client.SendAsync(Request(HttpMethod.Patch, "/api/v1/admin/cta/submissions/subm_c", Manage, new { assigneeId = "" }))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get("/api/v1/admin/cta/submissions/subm_c")).GetProperty("assigneeId").ValueKind.Should().Be(JsonValueKind.Null);

        (await _client.SendAsync(Request(HttpMethod.Patch, "/api/v1/admin/cta/submissions/subm_c", Manage, new { status = "new" }))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditRows()).Count(a => a.Action == "update").Should().Be(1);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"bogus\"}")]
    public async Task InvalidPatchesAreRejectedPerField(string json)
    {
        await Seed("subm_v");
        using var request = Request(HttpMethod.Patch, "/api/v1/admin/cta/submissions/subm_v", Manage);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReadingListingAndExportingAreAuditedWithoutRecordingSearchText()
    {
        await Seed("subm_r");
        await Get("/api/v1/admin/cta/submissions?q=Rahim-secret-name");
        await Get("/api/v1/admin/cta/submissions/subm_r");
        using var export = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/admin/cta/submissions/export", Export));
        export.EnsureSuccessStatusCode();

        var rows = await AuditRows();
        rows.Select(a => a.Action).Should().Equal("list", "view", "export");
        rows.Should().OnlyContain(a => a.Actor == "usr_test");
        rows[1].SubmissionId.Should().Be("subm_r");
        rows[0].Detail.Should().Contain("q=<text>").And.NotContain("Rahim-secret-name");
    }

    [Fact]
    public async Task CsvExportOpensInExcelWithBengaliIntact()
    {
        await Seed("subm_csv", first: "রহিম", last: "উদ্দিন", language: "bn",
            values: "{\"firstName\":\"রহিম\",\"lastName\":\"উদ্দিন\",\"email\":\"r@example.com\",\"phone\":\"+8801712345678\",\"message\":\"=cmd|' /C calc'!A0, \\\"hi\\\"\"}");

        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/admin/cta/submissions/export", Export));

        response.Content.Headers.ContentType!.ToString().Should().Be("text/csv; charset=utf-8");
        response.Content.Headers.ContentDisposition!.FileName.Should().StartWith("cta-submissions-").And.EndWith(".csv");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes[..3].Should().Equal(0xEF, 0xBB, 0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().Contain("রহিম,উদ্দিন,r@example.com,+8801712345678,");
        text.Should().Contain("\"'=cmd|' /C calc'!A0, \"\"hi\"\"\"", "formula-looking text is neutralised and quoting is correct");
        text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
    }

    [Fact]
    public async Task ExportHonoursTheListFilters()
    {
        await Seed("subm_keep", status: "new");
        await Seed("subm_drop", status: "spam");

        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/admin/cta/submissions/export?status=new", Export));

        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("subm_keep").And.NotContain("subm_drop");
    }
}

public sealed class SubmissionAntiSpamTests
{
    private sealed class FakeTurnstile : ITurnstileVerifier
    {
        public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken) => Task.FromResult(token == "good");
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static Task<HttpResponseMessage> Post(HttpClient client, Dictionary<string, object?>? values = null, DateTimeOffset? opened = null, string? token = null) =>
        client.PostAsJsonAsync("/api/v1/cta/submissions", new
        {
            formName = "contact",
            ctaType = "contact",
            language = "en",
            sourcePath = "/",
            submittedAt = Now,
            values = values ?? new Dictionary<string, object?> { ["firstName"] = "Real Person" },
            formOpenedAt = opened,
            turnstileToken = token
        });

    private static async Task<int> Stored(WebApplicationFactory<CtaSubmissionService.CtaSubmissionServiceTestEntryPoint> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CtaDbContext>().Submissions.CountAsync(s => s.Id != "subm_dev_bengali");
    }

    [Fact]
    public async Task AFilledHoneypotIsRejectedAndNothingIsStored()
    {
        using var factory = new CtaApiFactory();
        using var client = factory.CreateClient();

        using var response = await Post(client, new Dictionary<string, object?> { ["firstName"] = "Bot", ["website"] = "http://spam.example" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("SUBMISSION_REJECTED");
        (await Stored(factory)).Should().Be(0);
    }

    [Fact]
    public async Task AnEmptyHoneypotIsFine()
    {
        using var factory = new CtaApiFactory();
        using var client = factory.CreateClient();

        using var response = await Post(client, new Dictionary<string, object?> { ["firstName"] = "Human", ["website"] = "" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData(1, HttpStatusCode.BadRequest)]
    [InlineData(2.9, HttpStatusCode.BadRequest)]
    [InlineData(-60, HttpStatusCode.BadRequest)]
    [InlineData(3, HttpStatusCode.Created)]
    [InlineData(45, HttpStatusCode.Created)]
    public async Task SubmissionsFasterThanAHumanCanFillTheFormAreRejected(double secondsToFill, HttpStatusCode expected)
    {
        using var factory = new CtaApiFactory();
        using var client = factory.CreateClient();

        using var response = await Post(client, opened: Now.AddSeconds(-secondsToFill));

        response.StatusCode.Should().Be(expected);
        (await Stored(factory)).Should().Be(expected == HttpStatusCode.Created ? 1 : 0);
    }

    [Fact]
    public async Task ClientsThatDoNotSendTheTimestampYetStillWork()
    {
        using var factory = new CtaApiFactory();
        using var client = factory.CreateClient();

        (await Post(client)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task TurnstileIsEnforcedOnlyWhenConfigured()
    {
        using var open = new CtaApiFactory();
        using var openClient = open.CreateClient();
        (await Post(openClient)).StatusCode.Should().Be(HttpStatusCode.Created);

        using var guarded = new CtaApiFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("AntiSpam:Turnstile:SecretKey", "secret");
            b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Transient<ITurnstileVerifier, FakeTurnstile>()));
        });
        using var client = guarded.CreateClient();

        (await Post(client)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Post(client, token: "bad")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Post(client, token: "good")).StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
