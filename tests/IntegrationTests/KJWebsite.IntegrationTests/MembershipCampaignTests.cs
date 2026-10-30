using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class MembershipCampaignTests : IDisposable
{
    private const string Admin = "/api/v1/admin/membership/campaigns";
    private const string Email = "season.applicant@example.org";

    private readonly MembershipApiFactory _factory = new();
    private readonly HttpClient _client;

    public MembershipCampaignTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task WithoutCampaignsIntakeIsUnrestricted()
    {
        var state = await Json(await _client.GetAsync("/api/v1/membership/campaigns/current"));

        state.GetProperty("intake").GetString().Should().Be("unrestricted");
        (await Apply()).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task OpenCampaignAcceptsAndLinksApplications()
    {
        var id = await CreateCampaign("boshonto-1434", opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(30));

        var state = await Json(await _client.GetAsync("/api/v1/membership/campaigns/current"));
        state.GetProperty("intake").GetString().Should().Be("open");
        state.GetProperty("campaign").GetProperty("name").GetProperty("bn").GetString().Should().Be("বসন্ত");
        using var response = await Apply();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Applications.SingleAsync()).CampaignId.Should().Be(id);
    }

    [Fact]
    public async Task ClosingASeasonStopsIntakeWithAClearError()
    {
        var id = await CreateCampaign("s1", DateTimeOffset.UtcNow.AddDays(-1), null);

        using var close = await Send(HttpMethod.Post, $"{Admin}/{id}/close");
        using var again = await Send(HttpMethod.Post, $"{Admin}/{id}/close");
        using var response = await Apply();

        close.StatusCode.Should().Be(HttpStatusCode.OK);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("INTAKE_CLOSED");
        (await Json(await _client.GetAsync("/api/v1/membership/campaigns/current"))).GetProperty("intake").GetString().Should().Be("closed");
    }

    [Fact]
    public async Task TheCurrentEndpointAlsoSpeaksTheSitesShape()
    {
        var unrestricted = await Json(await _client.GetAsync("/api/v1/membership/campaigns/current"));
        unrestricted.GetProperty("state").ValueKind.Should().Be(JsonValueKind.Null, "the site then keeps its static form");

        var id = await CreateCampaign("shape", DateTimeOffset.UtcNow.AddDays(-1), new DateTimeOffset(2099, 3, 4, 0, 0, 0, TimeSpan.Zero));
        var open = await Json(await _client.GetAsync("/api/v1/membership/campaigns/current"));
        open.GetProperty("state").GetString().Should().Be("open");
        open.GetProperty("id").GetString().Should().Be(id);
        open.GetProperty("name").GetProperty("en").GetString().Should().Be("Spring");
        open.GetProperty("closesOn").GetString().Should().Be("2099-03-04");

        (await Send(HttpMethod.Post, $"{Admin}/{id}/close")).Dispose();
        await CreateCampaign("next", DateTimeOffset.UtcNow.AddDays(10), null);
        var upcoming = await Json(await _client.GetAsync("/api/v1/membership/campaigns/current"));
        upcoming.GetProperty("state").GetString().Should().Be("upcoming");
        upcoming.GetProperty("opensOn").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
        upcoming.GetProperty("intake").GetString().Should().Be("closed");
    }

    [Fact]
    public async Task AWindowInThePastOrFutureIsClosed()
    {
        await CreateCampaign("past", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        await CreateCampaign("future", DateTimeOffset.UtcNow.AddDays(5), null);

        (await Apply()).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CampaignAdminNeedsThePermission()
    {
        (await _client.GetAsync(Admin)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var forbidden = await Send(HttpMethod.Get, Admin, TestTokens.Issue([Permissions.ContentView]));
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CampaignInputIsValidatedAndSlugsAreUnique()
    {
        await CreateCampaign("dup", DateTimeOffset.UtcNow, null);

        using var bad = await Send(HttpMethod.Post, Admin, body: new { slug = "Bad Slug", nameEn = "", opensAt = DateTimeOffset.UtcNow, closesAt = DateTimeOffset.UtcNow.AddDays(-1) });
        using var dup = await Send(HttpMethod.Post, Admin, body: new { slug = "dup", nameEn = "x", nameBn = "y", opensAt = DateTimeOffset.UtcNow });

        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await bad.Content.ReadAsStringAsync();
        text.Should().Contain("slug").And.Contain("nameEn").And.Contain("nameBn").And.Contain("closesAt");
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CampaignChangesAreAudited()
    {
        var id = await CreateCampaign("audited", DateTimeOffset.UtcNow, null);
        (await Send(HttpMethod.Post, $"{Admin}/{id}/close")).Dispose();

        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Set<KJWebsite.BuildingBlocks.Auditing.AuditEvent>().ToListAsync();

        rows.Should().Contain(r => r.Entity == "Campaign" && r.Action == "create");
        rows.Should().Contain(r => r.Entity == "Campaign" && r.Action == "update");
    }

    [Fact]
    public async Task StatusLookupReturnsTheTimelineOnly()
    {
        var created = await Json(await Apply());

        using var response = await Lookup(created.GetProperty("reference").GetString()!, Email.ToUpperInvariant());
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var timeline = JsonDocument.Parse(text).RootElement.GetProperty("timeline");
        timeline.GetArrayLength().Should().Be(1);
        timeline[0].GetProperty("status").GetString().Should().Be("awaiting");
        timeline[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("status", "at");
        text.ToLowerInvariant().Should().NotContain("note").And.NotContain("score");
    }

    [Fact]
    public async Task EveryMismatchIsIndistinguishable()
    {
        var reference = (await Json(await Apply())).GetProperty("reference").GetString()!;

        var unknown = await Lookup("KJ-2026-ZZZZZZZZ", Email);
        var wrongEmail = await Lookup(reference, "someone.else@example.org");
        var malformed = await Lookup("", "nope");

        foreach (var r in new[] { unknown, wrongEmail, malformed })
        {
            r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        var bodies = new[] { unknown, wrongEmail, malformed }.Select(r => Normalise(r.Content.ReadAsStringAsync().Result)).Distinct();
        bodies.Should().HaveCount(1);
    }

    [Fact]
    public async Task ReferencesAreNotSequential()
    {
        var refs = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            refs.Add((await Json(await Apply())).GetProperty("reference").GetString()!);
        }

        refs.Distinct().Should().HaveCount(5);
        refs.Select(r => r[^8..]).Should().NotBeInAscendingOrder().And.NotBeInDescendingOrder();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // The instance path differs by request only through the path, which is the same endpoint.
    private static string Normalise(string body) => System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", string.Empty);

    private async Task<string> CreateCampaign(string slug, DateTimeOffset opensAt, DateTimeOffset? closesAt)
    {
        using var response = await Send(HttpMethod.Post, Admin, body: new { slug, nameEn = "Spring", nameBn = "বসন্ত", opensAt, closesAt });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.Issue([Permissions.MembershipManage]));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Apply() => _client.PostAsJsonAsync("/api/v1/membership/applications", new
    {
        firstName = "Tahmid",
        lastName = "Rahman",
        email = Email,
        phone = "01977887087",
        bloodGroup = "O+",
        currentAddress = "Dhaka",
        academicInstitute = "DU",
        occupation = "Student",
        language = "en"
    });

    private Task<HttpResponseMessage> Lookup(string reference, string email) =>
        _client.PostAsJsonAsync("/api/v1/membership/applications/status", new { reference, email });

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}
