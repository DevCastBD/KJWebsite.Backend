using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;

namespace KJWebsite.IntegrationTests;

public sealed class SiteSettingsTests : IDisposable
{
    private const string Admin = "/api/v1/admin/content";
    private const string Public = "/api/v1/content";
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;

    public SiteSettingsTests() => _client = _factory.CreateClient();

    private static readonly object Nav = new object[]
    {
        new { id = "about", label = new { bn = "সম্পর্কে", en = "About" }, url = "/about", children = new[] { new { id = "team", label = new { bn = "দল", en = "Team" }, url = "/about/team" } } },
        new { id = "news", label = new { bn = "খবর", en = "News" }, url = "/news", children = Array.Empty<object>() }
    };

    [Fact]
    public async Task NavigationAndContactChangesAppearPubliclyInTheRightLanguageWithoutADeployment()
    {
        (await Put("navigation.main", Nav)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Put("contact", new { address = new { bn = "ঢাকা", en = "Dhaka" }, email = "hello@example.org", phone = "+880 1" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Put("homepage.sections", new[] { "hero", "projects", "news" })).StatusCode.Should().Be(HttpStatusCode.OK);

        using var bn = await Json($"{Public}/settings?lang=bn");
        var settings = bn.RootElement.GetProperty("settings");
        settings.GetProperty("navigation.main")[0].GetProperty("label").GetString().Should().Be("সম্পর্কে");
        settings.GetProperty("navigation.main")[0].GetProperty("children")[0].GetProperty("label").GetString().Should().Be("দল");
        settings.GetProperty("contact").GetProperty("address").GetString().Should().Be("ঢাকা");
        settings.GetProperty("homepage.sections").EnumerateArray().Select(s => s.GetString()).Should().Equal("hero", "projects", "news");
        using var en = await Json($"{Public}/settings?lang=en");
        en.RootElement.GetProperty("settings").GetProperty("contact").GetProperty("address").GetString().Should().Be("Dhaka");

        // A later change shows at once even though the response was cached.
        await Put("footer.text", new { bn = "পাদটীকা", en = "Footer" });
        using var after = await Json($"{Public}/settings?lang=en");
        after.RootElement.GetProperty("settings").GetProperty("footer.text").GetString().Should().Be("Footer");
        using var bootstrap = await Json($"{Public}/bootstrap?lang=bn");
        bootstrap.RootElement.GetProperty("settings").GetProperty("footer.text").GetString().Should().Be("পাদটীকা");
    }

    [Fact]
    public async Task BothLanguagesAreRequiredBeforeSaving()
    {
        (await Put("footer.text", new { bn = "শুধু বাংলা" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put("footer.text", new { bn = " ", en = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put("navigation.main", new[] { new { id = "a", label = new { bn = "ক" }, url = "/a" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put("navigation.main", new[] { new { id = "a", label = new { bn = "ক", en = "A" }, url = "javascript:alert(1)" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put("navigation.main", new[] { new { id = "a", label = new { bn = "ক", en = "A" }, url = "//evil.example" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put("navigation.main", new[] { new { id = "a", label = new { bn = "ক", en = "A" }, url = "/a" }, new { id = "a", label = new { bn = "খ", en = "B" }, url = "/b" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "duplicate ids");
        (await Put("contact", new { address = new { bn = "ঢাকা", en = "Dhaka" }, email = "not-an-email" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put("social", new[] { new { network = "facebook", url = "ftp://x" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put("homepage.sections", new[] { "a", "a" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put("maintenance", new { enabled = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a maintenance message is needed in both languages");
        (await Put("nonsense.key", new { bn = "ক", en = "A" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put("footer.text", "just a string")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json($"{Public}/settings")).RootElement.GetProperty("settings").TryGetProperty("footer.text", out _).Should().BeFalse("nothing invalid was stored");
    }

    [Fact]
    public async Task SecretSettingsAreNeverExposedPublicly()
    {
        (await Put("custom.api-note", new { bn = "গোপন-বাংলা", en = "TOP-SECRET-VALUE" }, secret: true)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Put("custom.public-note", new { bn = "খোলা", en = "open" })).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var path in new[] { $"{Public}/settings?lang=en", $"{Public}/settings?lang=bn", $"{Public}/bootstrap?lang=en" })
        {
            var text = await _client.GetStringAsync(path);
            text.Should().NotContain("TOP-SECRET-VALUE").And.NotContain("গোপন-বাংলা").And.NotContain("custom.api-note", path);
            text.Should().Contain("custom.public-note");
        }

        using var list = await Json($"{Admin}/settings", Token());
        var secret = list.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == "custom.api-note");
        secret.GetProperty("isSecret").GetBoolean().Should().BeTrue();
        secret.TryGetProperty("value", out var value).Should().BeTrue();
        value.ValueKind.Should().Be(JsonValueKind.Null, "lists hide secret values");
        using var single = await Json($"{Admin}/settings/custom.api-note", Token());
        single.RootElement.GetProperty("value").GetProperty("en").GetString().Should().Be("TOP-SECRET-VALUE");
        (await Put("contact", new { address = new { bn = "ক", en = "A" }, email = "a@b.c" }, secret: true)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "catalog settings are public by design");
    }

    [Fact]
    public async Task MaintenanceModeIsReportedNotEnforcedSoTheFrontendKeepsItsFallback()
    {
        using (var off = await Json($"{Public}/settings"))
        {
            off.RootElement.GetProperty("settings").GetProperty("maintenance").GetProperty("enabled").GetBoolean().Should().BeFalse();
        }

        await Put("maintenance", new { enabled = true, message = new { bn = "রক্ষণাবেক্ষণ", en = "Back soon" } });

        using var on = await Json($"{Public}/settings?lang=en");
        on.RootElement.GetProperty("settings").GetProperty("maintenance").GetProperty("message").GetString().Should().Be("Back soon");
        (await _client.GetAsync($"{Public}/projects")).StatusCode.Should().Be(HttpStatusCode.OK, "content endpoints stay up");
        (await _client.GetAsync($"{Public}/bootstrap")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RuntimeFlagsAreSwitchedWithoutADeployAndKeptSeparateFromSettings()
    {
        (await Flag("new-donate-page", new { enabled = true, description = "Show the new donate page" })).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var on = await Json($"{Public}/settings"))
        {
            on.RootElement.GetProperty("flags").GetProperty("new-donate-page").GetBoolean().Should().BeTrue();
        }

        await Flag("new-donate-page", new { enabled = false });
        using (var off = await Json($"{Public}/settings"))
        {
            off.RootElement.GetProperty("flags").GetProperty("new-donate-page").GetBoolean().Should().BeFalse();
        }

        using var list = await Json($"{Admin}/flags", Token());
        list.RootElement.GetProperty("items")[0].GetProperty("description").GetString().Should().Be("Show the new donate page", "an update without a description keeps it");
        (await Flag("Bad Key", new { enabled = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Delete, $"{Admin}/flags/new-donate-page", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Delete, $"{Admin}/flags/new-donate-page", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ConcurrentEditsAreDetectedAndPermissionsAreEnforced()
    {
        await Put("footer.text", new { bn = "এক", en = "One" });
        using var read = await Send(HttpMethod.Get, $"{Admin}/settings/footer.text", null);
        var tag = read.Headers.ETag!.Tag;

        (await Put("footer.text", new { bn = "দুই", en = "Two" }, ifMatch: tag)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Put("footer.text", new { bn = "তিন", en = "Three" }, ifMatch: tag)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var viewer = TestTokens.Issue([Permissions.SettingsView]);
        (await Send(HttpMethod.Get, $"{Admin}/settings", null, viewer)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Put, $"{Admin}/settings/footer.text", new { value = new { bn = "ক", en = "A" } }, viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Put, $"{Admin}/flags/x1", new { enabled = true }, viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Get, $"{Admin}/settings", null, TestTokens.ContentAdmin())).StatusCode.Should().Be(HttpStatusCode.Forbidden, "content.* is not settings.*");
        (await _client.GetAsync($"{Admin}/settings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Delete, $"{Admin}/settings/footer.text", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Get, $"{Admin}/settings/footer.text", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SettingChangesAreAudited()
    {
        await Put("footer.text", new { bn = "এক", en = "One" });
        await Put("footer.text", new { bn = "দুই", en = "Two" });

        using var audit = await Json($"{Admin}/audit?entity=Setting&action=update", TestTokens.Issue([Permissions.AuditView]));
        audit.RootElement.GetProperty("items").EnumerateArray().Should().ContainSingle(e => e.GetProperty("before").GetProperty("valueJson").GetString()!.Contains("One"));
    }

    private static string Token() => TestTokens.Issue([Permissions.SettingsView, Permissions.SettingsManage]);

    private Task<HttpResponseMessage> Put(string key, object value, bool? secret = null, string? ifMatch = null) =>
        Send(HttpMethod.Put, $"{Admin}/settings/{key}", new { value, isSecret = secret }, ifMatch: ifMatch);

    private Task<HttpResponseMessage> Flag(string key, object body) => Send(HttpMethod.Put, $"{Admin}/flags/{key}", body);

    private async Task<JsonDocument> Json(string path, string? token = null)
    {
        using var response = token is null ? await _client.GetAsync(path) : await Send(HttpMethod.Get, path, null, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, string? token = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token());
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

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
