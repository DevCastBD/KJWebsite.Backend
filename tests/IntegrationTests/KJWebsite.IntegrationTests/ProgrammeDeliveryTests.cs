using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;

namespace KJWebsite.IntegrationTests;

public sealed class ProgrammeDeliveryTests : IDisposable
{
    private const string Root = "/api/v1/admin/membership/programme";
    private readonly MembershipApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _manager = TestTokens.Issue([Permissions.ProgrammeManage, Permissions.ProgrammeLog], subject: "usr_manager");
    private readonly string _field = TestTokens.Issue([Permissions.ProgrammeLog], subject: "usr_field");

    public ProgrammeDeliveryTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task ProjectShowsInstitutionsMousSessionsAndStudentsReached()
    {
        var school = await Institution("pathshala-a", "Dhaka", "House 5, Road 3, Mirpur");
        var madrasa = await Institution("madrasa-b", "Gazipur", null);
        (await Send(_manager, HttpMethod.Put, $"{Root}/beneficiaries/{school}/enrolments/pathshala", new { students = 40 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(_manager, HttpMethod.Put, $"{Root}/beneficiaries/{madrasa}/enrolments/pathshala", new { students = 25 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(_manager, HttpMethod.Post, $"{Root}/beneficiaries/{school}/mous", new { signedOn = "2026-01-10", validUntil = "2027-01-09", signatory = "Headmaster Abdul", signatoryRole = "Head" })).StatusCode.Should().Be(HttpStatusCode.Created);
        await Log("c1", "pathshala", "pathshala-a", 30);
        await Log("c2", "pathshala", "madrasa-b", 20);
        await Log("c3", "other-project", "pathshala-a", 10);

        using var summary = await Send(_manager, HttpMethod.Get, $"{Root}/projects/pathshala/summary");
        var root = JsonDocument.Parse(await summary.Content.ReadAsStringAsync()).RootElement;
        root.GetProperty("studentsReached").GetInt32().Should().Be(65);
        root.GetProperty("sessionsDelivered").GetInt32().Should().Be(2);
        root.GetProperty("attendanceTotal").GetInt32().Should().Be(50);
        root.GetProperty("institutions").GetArrayLength().Should().Be(2);
        root.GetProperty("mous")[0].GetProperty("signatory").GetString().Should().Be("Headmaster Abdul");
    }

    [Fact]
    public async Task SessionLogsAcceptDelayedAndBatchedSubmissionIdempotently()
    {
        await Institution("remote-school", "Sylhet", null);
        var old = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-60).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var batch = new
        {
            sessions = new object[]
            {
                Session("b1", "pathshala", "remote-school", old, 15),
                Session("b2", "pathshala", "remote-school", old, 18),
                Session("b1", "pathshala", "remote-school", old, 15),
                Session("bad", "pathshala", "no-such-school", old, 5),
                Session("future", "pathshala", "remote-school", "2999-01-01", 5)
            }
        };

        using var first = await Send(_field, HttpMethod.Post, $"{Root}/sessions/batch", batch);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("result").GetString()).ToList();
        results.Should().Equal("created", "created", "duplicate", "invalid", "invalid");

        using var replay = await Send(_field, HttpMethod.Post, $"{Root}/sessions/batch", batch);
        JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("result").GetString()).Take(3).Should().Equal("duplicate", "duplicate", "duplicate");

        using var list = await Send(_manager, HttpMethod.Get, $"{Root}/sessions?project=pathshala");
        JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("total").GetInt32().Should().Be(2, "replaying the batch added nothing");
    }

    [Fact]
    public async Task FieldStaffSeeNoInstitutionDataAndPublishedFiguresAreAggregatesOnly()
    {
        var school = await Institution("secret-school", "Rangpur", "Secret Lane 9");
        await Send(_manager, HttpMethod.Put, $"{Root}/beneficiaries/{school}/enrolments/pathshala", new { students = 50 });
        await Send(_manager, HttpMethod.Post, $"{Root}/beneficiaries/{school}/mous", new { signedOn = "2026-02-01", signatory = "Principal Karim" });
        await Log("f1", "pathshala", "secret-school", 22);

        (await Send(_field, HttpMethod.Get, $"{Root}/beneficiaries")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(_field, HttpMethod.Get, $"{Root}/beneficiaries/{school}/mous")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(_field, HttpMethod.Get, $"{Root}/figures")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.GetAsync($"{Root}/figures")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var figures = await Send(_manager, HttpMethod.Get, $"{Root}/figures");
        var text = await figures.Content.ReadAsStringAsync();
        text.Should().NotContain("Secret").And.NotContain("Principal").And.NotContain("Rangpur").And.NotContain("secret-school");
        var root = JsonDocument.Parse(text).RootElement;
        root.GetProperty("studentsReached").GetInt32().Should().Be(50);
        root.GetProperty("sessionsDelivered").GetInt32().Should().Be(1);
        root.GetProperty("fingerprint").GetString().Should().HaveLength(16);
    }

    [Fact]
    public async Task MouDocumentsAreSignatureCheckedAndPermissionGated()
    {
        var school = await Institution("docs-school", "Dhaka", null);
        using var created = await Send(_manager, HttpMethod.Post, $"{Root}/beneficiaries/{school}/mous", new { signedOn = "2026-03-01", signatory = "Someone" });
        var mouId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        (await Upload(_manager, mouId, "application/pdf", "not a pdf"u8.ToArray())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Upload(_manager, mouId, "text/plain", "%PDF-1.4"u8.ToArray())).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await Upload(_field, mouId, "application/pdf", "%PDF-1.4 mou"u8.ToArray())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Upload(_manager, mouId, "application/pdf", "%PDF-1.4 signed mou"u8.ToArray())).StatusCode.Should().Be(HttpStatusCode.OK);

        using var download = await Send(_manager, HttpMethod.Get, $"{Root}/mous/{mouId}/document");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Headers.CacheControl!.NoStore.Should().BeTrue();
        Encoding.ASCII.GetString(await download.Content.ReadAsByteArrayAsync()).Should().StartWith("%PDF-1.4");
        (await Send(_field, HttpMethod.Get, $"{Root}/mous/{mouId}/document")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.GetAsync($"{Root}/mous/{mouId}/document")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OutcomesAreRecordedPerProjectAndPeriod()
    {
        var body = new { projectSlug = "pathshala", metricKey = "literacy-gain", periodStart = "2026-01-01", periodEnd = "2026-06-30", value = 12.5, unit = "percent" };
        (await Send(_manager, HttpMethod.Put, $"{Root}/outcomes", body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(_manager, HttpMethod.Put, $"{Root}/outcomes", new { projectSlug = "pathshala", metricKey = "literacy-gain", periodStart = "2026-01-01", periodEnd = "2026-06-30", value = 14.0, unit = "percent" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(_manager, HttpMethod.Put, $"{Root}/outcomes", new { projectSlug = "pathshala", metricKey = "x", periodStart = "2026-06-30", periodEnd = "2026-01-01", value = 1, unit = "n" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var summary = await Send(_manager, HttpMethod.Get, $"{Root}/projects/pathshala/summary");
        var outcomes = JsonDocument.Parse(await summary.Content.ReadAsStringAsync()).RootElement.GetProperty("outcomes");
        outcomes.GetArrayLength().Should().Be(1, "the same metric and period is replaced, not duplicated");
        outcomes[0].GetProperty("value").GetDouble().Should().Be(14.0);
    }

    private async Task<string> Institution(string slug, string district, string? address)
    {
        using var response = await Send(_manager, HttpMethod.Post, $"{Root}/beneficiaries", new { slug, kind = "school", nameEn = slug, nameBn = "বিদ্যালয়", district, address });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private async Task Log(string clientId, string project, string beneficiary, int attendance)
    {
        var held = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        (await Send(_field, HttpMethod.Post, $"{Root}/sessions", Session(clientId, project, beneficiary, held, attendance))).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static object Session(string clientId, string project, string beneficiary, string heldOn, int attendance) =>
        new { clientId, projectSlug = project, beneficiarySlug = beneficiary, heldOn, topic = "Reading", attendance, durationMinutes = 60 };

    private async Task<HttpResponseMessage> Upload(string token, string mouId, string contentType, byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Root}/mous/{mouId}/document") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> Send(string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
