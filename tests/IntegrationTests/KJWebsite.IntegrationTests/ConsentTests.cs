using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace KJWebsite.IntegrationTests;

public sealed class ConsentTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _staff = TestTokens.Issue([Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate, Permissions.ContentPublish]);
    private readonly string _editor = TestTokens.Issue([Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate]);

    public ConsentTests() => _client = _factory.CreateClient();

    private static readonly object Consent = new { subjectName = "Rahima Begum", basis = "written", scope = "Quote and first name on the website", evidence = "Form KJ-2026-114, filing cabinet B" };

    [Fact]
    public async Task NothingPublishesWithoutAConsentRecordOnAnyRoute()
    {
        // created as published, no consent: refused
        (await Create("rahima", "published")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Create("rahima", "draft")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Workflow("rahima", "published")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Workflow("rahima", "scheduled", DateTimeOffset.UtcNow.AddDays(1))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var viaPut = await Send(_staff, HttpMethod.Put, $"{Admin}/testimonials/rahima", Body("rahima", "published"));
        viaPut.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(await Get($"{Public}/testimonials?lang=en"))).GetProperty("items").GetArrayLength().Should().Be(0);

        // incomplete evidence is rejected
        (await Send(_editor, HttpMethod.Post, $"{Admin}/items/testimonial/rahima/consents", new { subjectName = "R", basis = "written" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(_editor, HttpMethod.Post, $"{Admin}/items/testimonial/rahima/consents", Consent)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await Workflow("rahima", "published")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await Get($"{Public}/testimonials?lang=en"))).GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task WithdrawalUnpublishesAtOnceListsThePathAndBlocksRepublishing()
    {
        await Create("karim", "draft");
        var consentId = await Record("karim", Consent);
        (await Workflow("karim", "published")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get($"{Public}/testimonials/karim?lang=en")).StatusCode.Should().Be(HttpStatusCode.OK);

        // an editor without publish rights can still honour a withdrawal
        using var withdrawal = await Send(_editor, HttpMethod.Post, $"{Admin}/consents/{consentId}/withdraw", new { note = "Asked by phone" });
        withdrawal.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(withdrawal)).GetProperty("unpublished").GetBoolean().Should().BeTrue();

        (await Get($"{Public}/testimonials/karim?lang=en")).StatusCode.Should().Be(HttpStatusCode.NotFound, "gone from the public API immediately, cache included");
        var feed = await Json(await Get($"{Public}/consent-withdrawals"));
        feed.EnumerateArray().Select(p => p.GetProperty("path").GetString()).Should().Contain("/testimonials/karim");
        feed.ToString().Should().NotContain("Rahima").And.NotContain("Asked by phone", "the feed carries no personal data");

        (await Workflow("karim", "published")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "withdrawn consent no longer counts");
        var records = await Json(await Send(_editor, HttpMethod.Get, $"{Admin}/items/testimonial/karim/consents", null));
        records[0].GetProperty("active").GetBoolean().Should().BeFalse();
        records[0].GetProperty("withdrawnBy").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AMinorsTestimonialNeedsAGuardianAndAnApprovedPolicy()
    {
        await Create("child", "draft");
        (await Send(_editor, HttpMethod.Post, $"{Admin}/items/testimonial/child/consents", new { subjectName = "A child", isMinor = true, basis = "written", scope = "Quote", evidence = "Form 9" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Record("child", new { subjectName = "A child", isMinor = true, guardianName = "Parent Name", basis = "guardian-written", scope = "Quote, no photo", evidence = "Form 9" });
        (await Json(await Workflow("child", "published"))).GetProperty("error").GetProperty("message").GetString().Should().Contain("policy");

        // once the organisation approves the policy the same record is enough
        using var approved = new ApprovedPolicyFactory();
        using var client = approved.CreateClient();

        async Task<HttpResponseMessage> Call(HttpMethod method, string url, object body)
        {
            using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new("Bearer", _staff);
            return await client.SendAsync(request);
        }

        (await Call(HttpMethod.Post, $"{Admin}/testimonials", Body("child", "draft"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Call(HttpMethod.Post, $"{Admin}/items/testimonial/child/consents", new { subjectName = "A child", isMinor = true, guardianName = "Parent Name", basis = "guardian-written", scope = "Quote, no photo", evidence = "Form 9" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Call(HttpMethod.Post, $"{Admin}/items/testimonial/child/workflow", new { to = "published" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class ApprovedPolicyFactory : SqliteWebApplicationFactory<ContentService.ContentServiceTestEntryPoint, ContentService.Data.ContentDbContext>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary),
                ["Newsletter:DispatchEnabled"] = "false",
                ["Consent:BeneficiaryPolicyApproved"] = "true"
            }));
        }
    }

    [Fact]
    public async Task PartnerLogoRightsAreRecordedAndGateTheLogo()
    {
        (await Send(_staff, HttpMethod.Post, $"{Admin}/partners", new { slug = "acme", status = "draft", translations = new object[] { new { lang = "en", title = "Acme Ltd" }, new { lang = "bn", title = "অ্যাকমি" } } })).StatusCode.Should().Be(HttpStatusCode.Created);
        await Record("acme", new { subjectName = "Acme Ltd", basis = "agreement", scope = "Listed as a partner", evidence = "Partnership agreement 2026-05" }, "partner");

        async Task<HttpResponseMessage> Put(object body) => await Send(_staff, HttpMethod.Put, $"{Admin}/partners/acme/details", body);
        (await Put(new { tier = "gold", websiteUrl = "https://acme.example", logoUrl = "https://acme.example/logo.png" })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no rights recorded yet");
        (await Put(new { tier = "gold", logoUrl = "https://acme.example/logo.png", logoRights = "restricted", logoEvidence = "Email 2026-06-01" })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "restricted needs conditions");
        (await Put(new { tier = "gold", websiteUrl = "https://acme.example", logoUrl = "https://acme.example/logo.png", logoRights = "restricted", logoConditions = "Footer only, no recolouring", logoEvidence = "Email 2026-06-01" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Workflow("acme", "published", type: "partner")).StatusCode.Should().Be(HttpStatusCode.OK);

        var shown = await Json(await Get($"{Public}/partners/acme/details"));
        shown.GetProperty("logoUrl").GetString().Should().Be("https://acme.example/logo.png");
        shown.GetProperty("logoConditions").ValueKind.Should().Be(JsonValueKind.Null, "conditions are internal");
        (await Json(await Get($"{Public}/partner-registry?lang=en"))).GetProperty("items")[0].GetProperty("tier").GetString().Should().Be("gold");

        (await Put(new { tier = "gold", logoRights = "withdrawn" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await Get($"{Public}/partners/acme/details"))).GetProperty("logoUrl").ValueKind.Should().Be(JsonValueKind.Null, "the logo disappears when rights are withdrawn");
    }

    [Fact]
    public async Task SuccessStoriesAreAContentTypeThatNeedsConsentToo()
    {
        (await Send(_staff, HttpMethod.Post, $"{Admin}/success-stories", new { slug = "story", status = "published", translations = new object[] { new { lang = "en", title = "T", summary = "S", body = "B" } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(_staff, HttpMethod.Post, $"{Admin}/success-stories", new { slug = "story", status = "draft", translations = new object[] { new { lang = "en", title = "T", summary = "S", body = "B" } } })).StatusCode.Should().Be(HttpStatusCode.Created);
        await Record("story", Consent, "success-story");
        (await Workflow("story", "published", type: "success-story")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get($"{Public}/success-stories/story?lang=en")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- helpers

    private static object Body(string slug, string status) => new { slug, status, translations = new object[] { new { lang = "en", title = "Quote", body = "Words" }, new { lang = "bn", title = "উদ্ধৃতি", body = "কথা" } } };

    private Task<HttpResponseMessage> Create(string slug, string status) => Send(_staff, HttpMethod.Post, $"{Admin}/testimonials", Body(slug, status));

    private Task<HttpResponseMessage> Workflow(string slug, string to, DateTimeOffset? at = null, string type = "testimonial") =>
        Send(_staff, HttpMethod.Post, $"{Admin}/items/{type}/{slug}/workflow", new { to, publishAt = at });

    private async Task<string> Record(string slug, object consent, string type = "testimonial")
    {
        using var response = await Send(_editor, HttpMethod.Post, $"{Admin}/items/{type}/{slug}/consents", consent);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> Get(string url) => _client.GetAsync(url);

    private async Task<HttpResponseMessage> Send(string token, HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
