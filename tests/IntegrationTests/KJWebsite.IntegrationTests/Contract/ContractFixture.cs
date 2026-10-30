using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests.Contract;

/// <summary>
/// Starts the real services (the same hosts that run in production, on Sqlite and in-memory jobs), exercises every operation the
/// spec documents, and records each place the live response disagrees with <c>openapi.v1.yaml</c>.
/// Run alone with <c>dotnet test --filter Category=Contract</c>.
/// </summary>
public sealed class ContractFixture : IAsyncLifetime
{
    private static readonly string[] AllContentPermissions =
        [Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate, Permissions.ContentDelete];

    private static readonly string[] CtaPermissions = [Permissions.CtaView, Permissions.CtaManage, Permissions.CtaExport];

    private readonly ConcurrentBag<string> _violations = [];
    private readonly ConcurrentDictionary<string, bool> _probed = new();

    internal ContractSpec Spec { get; } = ContractSpec.Load();

    public IReadOnlyCollection<string> Violations => _violations;

    public IReadOnlyCollection<string> Probed => [.. _probed.Keys];

    /// <summary>Endpoints each running service exposes, as "METHOD /route" (infrastructure routes excluded).</summary>
    public IReadOnlyCollection<string> RuntimeEndpoints { get; private set; } = [];

    public async Task InitializeAsync()
    {
        await using var content = new ContentApiFactory();
        await using var cta = new CtaApiFactory();
        await using var auth = new AuthApiFactory();
        await using var gateway = new GatewayApiFactory();

        await ProbeContentAsync(content.CreateClient());
        await ProbeCtaAsync(cta.CreateClient());
        await ProbeAuthAsync(auth.CreateClient());
        await Probe(gateway.CreateClient(), "GET", "/health", "/health", 200);

        RuntimeEndpoints = [.. Endpoints(content), .. Endpoints(cta), .. Endpoints(auth)];
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static IEnumerable<string> Endpoints<T>(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<T> factory) where T : class
    {
        var ignored = new[] { "/health", "/metrics", "/hangfire", "/openapi", "/scalar" };
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var route = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
            if (ignored.Any(i => route.StartsWith(i, StringComparison.Ordinal)))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var method in methods.Where(m => m != "OPTIONS"))
            {
                yield return $"{method} {route}";
            }
        }
    }

    // ---- probing

    /// <summary>One request, judged against the spec: the status must be documented and a documented JSON body must match its schema.</summary>
    private async Task<JsonNode?> Probe(HttpClient client, string method, string template, string url, int expected,
        string[]? permissions = null, object? body = null, string? bearer = null)
    {
        var key = $"{method} {template}";
        _probed[key] = true;
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (bearer is not null || permissions is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? TestTokens.Issue(permissions!));
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request);
        var status = (int)response.StatusCode;
        var label = $"{method} {template} → {status}";
        if (status != expected)
        {
            _violations.Add($"{label}: probe expected {expected} (the API behaved differently from what the probe assumed)");
        }

        var operation = Spec.Operation(method, template);
        if (operation is null)
        {
            _violations.Add($"{method} {template}: endpoint is not in openapi.v1.yaml");
            return null;
        }

        var documented = operation["responses"] as JsonObject;
        if (documented is null || !documented.ContainsKey(status.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            _violations.Add($"{label}: status is not documented (spec lists {string.Join(", ", documented?.Select(r => r.Key) ?? [])})");
        }

        var text = await response.Content.ReadAsStringAsync();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        JsonNode? json = null;
        if (mediaType is not null && mediaType.Contains("json", StringComparison.Ordinal) && text.Length > 0)
        {
            json = JsonNode.Parse(text);
        }

        var documentedContent = documented?[status.ToString(System.Globalization.CultureInfo.InvariantCulture)]?["content"] as JsonObject;
        if (documentedContent is not null)
        {
            var entry = documentedContent.FirstOrDefault(c => c.Key.Replace("\\u002B", "+", StringComparison.Ordinal) == mediaType);
            if (entry.Value is null)
            {
                _violations.Add($"{label}: content type '{mediaType}' is not documented (spec lists {string.Join(", ", documentedContent.Select(c => c.Key))})");
            }
            else if (json is not null && entry.Value["schema"] is { } schema)
            {
                foreach (var error in SchemaValidator.Validate(Spec, schema, json))
                {
                    _violations.Add($"{label}: {error}");
                }
            }
        }
        else if (documented is not null && documented.ContainsKey(status.ToString(System.Globalization.CultureInfo.InvariantCulture)) && text.Length > 0 && status is 204)
        {
            _violations.Add($"{label}: documented without a body but one was returned");
        }

        return json;
    }

    private async Task ProbeContentAsync(HttpClient client)
    {
        const string projects = "/api/v1/content/projects";
        const string news = "/api/v1/content/news";
        const string adminProjects = "/api/v1/admin/content/projects";
        const string adminNews = "/api/v1/admin/content/news";

        var projectList = await Probe(client, "GET", projects, projects, 200);
        await Probe(client, "GET", $"{projects}/{{id}}", $"{projects}/{projectList!["items"]![0]!["id"]}", 200);
        var newsList = await Probe(client, "GET", news, news, 200);
        await Probe(client, "GET", $"{news}/{{id}}", $"{news}/{newsList!["items"]![0]!["id"]}", 200);
        await Probe(client, "GET", "/api/v1/content/bootstrap", "/api/v1/content/bootstrap", 200);
        await Probe(client, "GET", $"{projects}/{{id}}", $"{projects}/does-not-exist", 404);
        await Probe(client, "GET", projects, $"{projects}?limit=0", 400);

        await Probe(client, "GET", adminProjects, adminProjects, 200, AllContentPermissions);
        await Probe(client, "GET", adminProjects, adminProjects, 401);
        await Probe(client, "GET", adminProjects, adminProjects, 403, [Permissions.CtaView]);
        var project = new { slug = "contract-project", status = "active", imageUrl = "/img/x.jpg", translations = new[] { new { lang = "en", title = "Contract", shortTitle = "C", shortDescription = "s", fullDescription = "f" } } };
        await Probe(client, "POST", adminProjects, adminProjects, 201, AllContentPermissions, project);
        await Probe(client, "POST", adminProjects, adminProjects, 400, AllContentPermissions, new { slug = "Bad Slug" });
        await Probe(client, "GET", $"{adminProjects}/{{id}}", $"{adminProjects}/contract-project", 200, AllContentPermissions);
        await Probe(client, "PUT", $"{adminProjects}/{{id}}", $"{adminProjects}/contract-project", 200, AllContentPermissions, project);
        await Probe(client, "DELETE", $"{adminProjects}/{{id}}", $"{adminProjects}/contract-project", 204, AllContentPermissions);

        await Probe(client, "GET", adminNews, adminNews, 200, AllContentPermissions);
        var article = new { slug = "contract-news", status = "published", imageUrl = "/img/n.jpg", publishedAt = DateTimeOffset.UtcNow, translations = new[] { new { lang = "en", title = "News", summary = "s", content = new[] { "p" } } } };
        await Probe(client, "POST", adminNews, adminNews, 201, AllContentPermissions, article);
        await Probe(client, "GET", $"{adminNews}/{{id}}", $"{adminNews}/contract-news", 200, AllContentPermissions);
        await Probe(client, "PUT", $"{adminNews}/{{id}}", $"{adminNews}/contract-news", 200, AllContentPermissions, article);
        await Probe(client, "DELETE", $"{adminNews}/{{id}}", $"{adminNews}/contract-news", 204, AllContentPermissions);
    }

    private async Task ProbeCtaAsync(HttpClient client)
    {
        const string submissions = "/api/v1/cta/submissions";
        const string admin = "/api/v1/admin/cta/submissions";
        var valid = new
        {
            formName = "contact",
            ctaType = "contact",
            language = "en",
            sourcePath = "/contact",
            submittedAt = DateTimeOffset.UtcNow,
            values = new Dictionary<string, object?> { ["firstName"] = "Contract", ["email"] = "contract@example.com", ["message"] = "Hello" }
        };
        var created = await Probe(client, "POST", submissions, submissions, 201, body: valid);
        await Probe(client, "POST", submissions, submissions, 400, body: new { formName = "bogus" });

        var id = created!["id"]!.ToString();
        await Probe(client, "GET", admin, admin, 200, CtaPermissions);
        await Probe(client, "GET", admin, admin, 401);
        await Probe(client, "GET", admin, $"{admin}?limit=0", 400, CtaPermissions);
        await Probe(client, "GET", $"{admin}/export", $"{admin}/export", 200, CtaPermissions);
        await Probe(client, "GET", $"{admin}/{{id}}", $"{admin}/{id}", 200, CtaPermissions);
        await Probe(client, "GET", $"{admin}/{{id}}", $"{admin}/missing", 404, CtaPermissions);
        await Probe(client, "PATCH", $"{admin}/{{id}}", $"{admin}/{id}", 200, CtaPermissions, new { status = "in_review", notes = "contract" });
        await Probe(client, "PATCH", $"{admin}/{{id}}", $"{admin}/{id}", 400, CtaPermissions, new { status = "bogus" });
    }

    private async Task ProbeAuthAsync(HttpClient client)
    {
        const string auth = "/api/v1/auth";
        var email = $"contract-{Guid.NewGuid():N}@example.test";

        await Probe(client, "POST", $"{auth}/register", $"{auth}/register", 201, body: new { email, password = "Str0ng!Passw0rd-x", firstName = "Contract" });
        await Probe(client, "POST", $"{auth}/register", $"{auth}/register", 400, body: new { email = "nope", password = "" });

        var login = await Probe(client, "POST", $"{auth}/login", $"{auth}/login", 200, body: new { email = "admin@site.org", password = "admin123" });
        await Probe(client, "POST", $"{auth}/login", $"{auth}/login", 401, body: new { email = "admin@site.org", password = "wrong-password" });
        var access = login!["access_token"]!.ToString();

        await Probe(client, "GET", $"{auth}/me", $"{auth}/me", 200, bearer: access);
        await Probe(client, "GET", $"{auth}/me", $"{auth}/me", 401);
        var sessions = await Probe(client, "GET", $"{auth}/sessions", $"{auth}/sessions", 200, bearer: access);
        await Probe(client, "GET", "/.well-known/jwks.json", "/.well-known/jwks.json", 200);

        var refreshed = await Probe(client, "POST", $"{auth}/refresh", $"{auth}/refresh", 200, body: new { refreshToken = login["refresh_token"]!.ToString() });
        await Probe(client, "POST", $"{auth}/refresh", $"{auth}/refresh", 401, body: new { refreshToken = "rt_unknown" });
        await Probe(client, "POST", $"{auth}/logout", $"{auth}/logout", 204, body: new { refreshToken = refreshed!["refresh_token"]!.ToString() });

        var second = await Probe(client, "POST", $"{auth}/login", $"{auth}/login", 200, body: new { email = "admin@site.org", password = "admin123" });
        var secondAccess = second!["access_token"]!.ToString();
        var list = await Probe(client, "GET", $"{auth}/sessions", $"{auth}/sessions", 200, bearer: secondAccess);
        await Probe(client, "DELETE", $"{auth}/sessions/{{id}}", $"{auth}/sessions/{list!["items"]![0]!["id"]}", 204, bearer: secondAccess);

        // The session above was just revoked, and with it the access token; sign in again.
        var third = await Probe(client, "POST", $"{auth}/login", $"{auth}/login", 200, body: new { email = "admin@site.org", password = "admin123" });
        await Probe(client, "DELETE", $"{auth}/sessions/{{id}}", $"{auth}/sessions/missing", 404, bearer: third!["access_token"]!.ToString());
        await Probe(client, "POST", $"{auth}/logout-all", $"{auth}/logout-all", 204, bearer: third!["access_token"]!.ToString());

        await Probe(client, "POST", $"{auth}/confirm-email", $"{auth}/confirm-email", 400, body: new { userId = "usr_none", token = "x" });
        await Probe(client, "POST", $"{auth}/resend-confirmation", $"{auth}/resend-confirmation", 202, body: new { email });
        await Probe(client, "POST", $"{auth}/forgot-password", $"{auth}/forgot-password", 202, body: new { email });
        await Probe(client, "POST", $"{auth}/reset-password", $"{auth}/reset-password", 400, body: new { email, token = "bad", newPassword = "Str0ng!Passw0rd-y" });
        _ = sessions;
    }
}
