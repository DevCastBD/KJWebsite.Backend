using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace KJWebsite.IntegrationTests;

public sealed class ValidationProblemTests : IDisposable
{
    private readonly CtaApiFactory _cta = new();
    private readonly AuthApiFactory _auth = new();
    private readonly ContentApiFactory _content = new();

    public void Dispose()
    {
        _cta.Dispose();
        _auth.Dispose();
        _content.Dispose();
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Errors(JsonElement body, string field) =>
        [.. body.GetProperty("errors").GetProperty(field).EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public async Task CtaSubmissionReportsEveryBadFieldWithStandardAndLegacyMembers()
    {
        using var client = _cta.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/cta/submissions", new
        {
            formName = "bogus",
            ctaType = "",
            language = "fr",
            sourcePath = "/x",
            values = new Dictionary<string, object?>(),
            submittedAt = DateTimeOffset.UtcNow
        });

        var body = await Body(response);

        body.GetProperty("type").GetString().Should().Be("urn:kjwebsite:error:validation-error");
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("instance").GetString().Should().Be("/api/v1/cta/submissions");
        Errors(body, "formName").Should().ContainSingle().Which.Should().Contain("formName");
        Errors(body, "ctaType").Should().NotBeEmpty();
        Errors(body, "language").Should().NotBeEmpty();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task MessagesAreAvailableInBengali()
    {
        using var client = _cta.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cta/submissions?lang=bn")
        {
            Content = JsonContent.Create(new { formName = "", ctaType = "contact", language = "bn", sourcePath = "/", values = new { }, submittedAt = DateTimeOffset.UtcNow })
        };
        using var response = await client.SendAsync(request);

        var body = await Body(response);

        Errors(body, "formName")[0].Should().Be("formName আবশ্যক।");
        body.GetProperty("detail").GetString().Should().Be("এক বা একাধিক ক্ষেত্র সঠিক নয়।");
    }

    [Fact]
    public async Task OversizedValuesPayloadIsRejected()
    {
        using var client = _cta.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/cta/submissions", new
        {
            formName = "contact",
            ctaType = "contact",
            language = "en",
            sourcePath = "/",
            values = new Dictionary<string, object?> { ["note"] = new string('x', 33_000) },
            submittedAt = DateTimeOffset.UtcNow
        });

        Errors(await Body(response), "values").Should().NotBeEmpty();
    }

    [Fact]
    public async Task RegisterValidatesEmailAndRequiredPasswordPerField()
    {
        using var client = _auth.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = "not-an-email", password = "" });

        var body = await Body(response);

        Errors(body, "email")[0].Should().Contain("valid email");
        Errors(body, "password").Should().NotBeEmpty();
    }

    [Fact]
    public async Task RegisterReportsDuplicateEmailAndWeakPasswordAsFieldErrors()
    {
        using var client = _auth.CreateClient();
        using var weak = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = "weak@example.com", password = "abc" });
        Errors(await Body(weak), "password").Should().NotBeEmpty();

        (await client.PostAsJsonAsync("/api/v1/auth/register", new { email = "dup@example.com", password = "Str0ng!Passw0rd-x" })).StatusCode.Should().Be(HttpStatusCode.Created);
        using var duplicate = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = "dup@example.com", password = "Str0ng!Passw0rd-x" });
        Errors(await Body(duplicate), "email")[0].Should().Contain("already in use");
    }

    [Fact]
    public async Task LoginAndResetRequireTheirFields()
    {
        using var client = _auth.CreateClient();

        Errors(await Body(await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "", password = "" })), "email").Should().NotBeEmpty();
        var reset = await Body(await client.PostAsJsonAsync("/api/v1/auth/reset-password", new { email = "a@example.com", token = "", newPassword = "" }));
        Errors(reset, "token").Should().NotBeEmpty();
        Errors(reset, "newPassword").Should().NotBeEmpty();
    }

    [Fact]
    public async Task ContentUpsertReportsNestedTranslationFields()
    {
        using var client = _content.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/content/projects")
        {
            Content = JsonContent.Create(new { slug = "Bad Slug", status = "nope", translations = new[] { new { lang = "xx", title = "" } } })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ContentAdmin());
        using var response = await client.SendAsync(request);

        var body = await Body(response);

        Errors(body, "slug").Should().NotBeEmpty();
        Errors(body, "status").Should().NotBeEmpty();
        Errors(body, "translations[0].lang").Should().NotBeEmpty();
        Errors(body, "translations[0].title").Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("/api/v1/content/projects?limit=0", "limit")]
    [InlineData("/api/v1/content/projects?limit=1000", "limit")]
    [InlineData("/api/v1/content/news?offset=-1", "offset")]
    [InlineData("/api/v1/content/projects?status=bogus", "status")]
    public async Task ListQueriesReportTheOffendingParameter(string url, string field)
    {
        using var client = _content.CreateClient();
        using var response = await client.GetAsync(url);

        Errors(await Body(response), field).Should().NotBeEmpty();
    }
}
