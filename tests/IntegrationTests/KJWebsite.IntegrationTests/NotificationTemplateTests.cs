using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentService.Data;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;

namespace KJWebsite.IntegrationTests;

public sealed class NotificationTemplateTests : IDisposable
{
    private const string Admin = "/api/v1/admin/notifications";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _staff = TestTokens.Issue([Permissions.CommsManage]);

    public NotificationTemplateTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task EveryCatalogueMessageHasBothLanguagesAndRendersCleanHtmlForOldMailClients()
    {
        var templates = await Json(await Send(HttpMethod.Get, $"{Admin}/templates", null));
        foreach (var spec in NotificationCatalogue.Specs)
        {
            foreach (var lang in new[] { "bn", "en" })
            {
                templates.EnumerateArray().Should().Contain(t => t.GetProperty("key").GetString() == spec.Key && t.GetProperty("lang").GetString() == lang && t.GetProperty("version").GetInt32() == 1, $"{spec.Key}/{lang} is seeded");
                var (subject, body) = NotificationCatalogue.Seed[(spec.Key, lang)];
                NewsletterRenderer.UsedVariables(subject + body).Should().BeSubsetOf(spec.Variables, $"{spec.Key}/{lang} only uses declared variables");

                var values = spec.Variables.ToDictionary(v => v, v => v == "name" ? "রহিম" : "https://example.org/" + v);
                using var response = await Send(HttpMethod.Post, $"{Admin}/templates/{spec.Key}/render", new { lang, values });
                response.StatusCode.Should().Be(HttpStatusCode.OK, $"{spec.Key}/{lang} renders");
                var rendered = await Json(response);
                var text = rendered.GetProperty("text").GetString()!;
                var html = rendered.GetProperty("html").GetString()!;
                (text + html + rendered.GetProperty("subject").GetString()).Should().NotContain("{{", "no placeholder survives");
                html.Should().Contain("<table").And.Contain("name=\"viewport\"").And.Contain("max-width:600px");
                html.Should().NotContain("<style").And.NotContain("display:flex").And.NotContain("display:grid").And.NotContain("<img", "plain, table-based markup survives Outlook, Gmail and phones");
            }
        }

        (await Json(await Send(HttpMethod.Get, $"{Admin}/templates", null))).GetArrayLength().Should().Be(NotificationCatalogue.Specs.Count * 2);
    }

    [Fact]
    public async Task AMissingVariableFailsTheRenderInsteadOfShowingBracesToARecipient()
    {
        using var response = await Send(HttpMethod.Post, $"{Admin}/templates/application.received/render", new { lang = "en", values = new { name = "Ana" }, record = true, toEmail = "ana@example.org" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("error").GetProperty("message").GetString().Should().Contain("reference");
        (await Json(await Send(HttpMethod.Get, $"{Admin}/sends", null))).GetArrayLength().Should().Be(0, "a failed render records nothing");

        (await Send(HttpMethod.Post, $"{Admin}/templates/nope/render", new { lang = "en", values = new { } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"{Admin}/templates/admin.alert/render", new { lang = "fr", values = new { } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EditsAreNewVersionsAndPastSendsStayReproducible()
    {
        var values = new { name = "Ana", reference = "APP-7" };
        using var first = await Send(HttpMethod.Post, $"{Admin}/templates/application.received/render", new { lang = "en", values, record = true, toEmail = "ana@example.org" });
        var firstRender = await Json(first);
        firstRender.GetProperty("version").GetInt32().Should().Be(1);
        var firstId = firstRender.GetProperty("sendId").GetInt64();

        // an edit that uses an undeclared variable is refused; a valid one becomes version 2
        (await Send(HttpMethod.Post, $"{Admin}/templates/application.received/en", new { subject = "Hi {{nickname}}", body = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var edit = await Send(HttpMethod.Post, $"{Admin}/templates/application.received/en", new { subject = "Got it, {{name}}", body = "Reference **{{reference}}**." });
        edit.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(edit)).GetProperty("version").GetInt32().Should().Be(2);

        var current = await Json(await Send(HttpMethod.Post, $"{Admin}/templates/application.received/render", new { lang = "en", values }));
        current.GetProperty("version").GetInt32().Should().Be(2);
        current.GetProperty("subject").GetString().Should().Be("Got it, Ana");

        var pinned = await Json(await Send(HttpMethod.Post, $"{Admin}/templates/application.received/render", new { lang = "en", values, version = 1 }));
        pinned.GetProperty("subject").GetString().Should().Be(firstRender.GetProperty("subject").GetString(), "version 1 still renders exactly as before");

        var sends = await Json(await Send(HttpMethod.Get, $"{Admin}/sends?key=application.received", null));
        var recorded = sends.EnumerateArray().Single(s => s.GetProperty("id").GetInt64() == firstId);
        recorded.GetProperty("version").GetInt32().Should().Be(1);
        recorded.GetProperty("subject").GetString().Should().Be(firstRender.GetProperty("subject").GetString());
        (await Json(await Send(HttpMethod.Get, $"{Admin}/templates/application.received/en/versions", null))).GetArrayLength().Should().Be(2);

        using var denied = new HttpRequestMessage(HttpMethod.Get, $"{Admin}/templates");
        denied.Headers.Authorization = new("Bearer", TestTokens.Issue([Permissions.ContentView]));
        (await _client.SendAsync(denied)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new("Bearer", _staff);
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
