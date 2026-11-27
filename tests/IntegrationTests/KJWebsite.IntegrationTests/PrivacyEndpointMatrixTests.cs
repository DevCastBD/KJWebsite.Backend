using System.Net;
using System.Security.Claims;
using System.Text;
using AuthIdentityService.Data;
using FinanceService.Data;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SponsorshipService.Data;

namespace KJWebsite.IntegrationTests;

public sealed class PrivacyEndpointMatrixTests(PrivacyStack stack) : IClassFixture<PrivacyStack>
{
    private sealed record Service(string Name, IServiceProvider Services, Func<HttpClient> CreateClient);
    private sealed record Route(Service Service, RouteEndpoint Endpoint, string Method)
    {
        public string Pattern => Endpoint.RoutePattern.RawText!;
        public string Label => $"{Method} {Pattern} ({Service.Name})";
        public string[] Permissions => Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(a => a.Policy).OfType<string>().Where(SecurityPermission).Distinct().ToArray();
        public bool DonorSelf => Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == KjPolicies.DonorSelf);
        private static bool SecurityPermission(string policy) => KJWebsite.BuildingBlocks.Security.Permissions.All.Contains(policy);
    }

    private Service[] Services() =>
    [
        new("auth", stack.Auth.Services, () => stack.Auth.CreateClient(ClientOptions)),
        new("content", stack.Content.Services, () => stack.Content.CreateClient(ClientOptions)),
        new("cta", stack.Cta.Services, () => stack.Cta.CreateClient(ClientOptions)),
        new("membership", stack.Membership.Services, () => stack.Membership.CreateClient(ClientOptions)),
        new("finance", stack.Finance.Services, () => stack.Finance.CreateClient(ClientOptions)),
        new("sponsorship", stack.Sponsorship.Services, () => stack.Sponsorship.CreateClient(ClientOptions))
    ];

    private static WebApplicationFactoryClientOptions ClientOptions => new() { AllowAutoRedirect = false, HandleCookies = false };
    private static bool Infrastructure(Route route) => new[] { "/health", "/openapi", "/swagger", "/scalar" }
        .Any(prefix => route.Pattern.Equals(prefix, StringComparison.OrdinalIgnoreCase) || route.Pattern.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    private static bool Anonymous(Route route) => route.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
    private static Route[] Routes(Service service) => service.Services.GetRequiredService<EndpointDataSource>().Endpoints
        .OfType<RouteEndpoint>()
        .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            .Select(method => new Route(service, endpoint, method)))
        .OrderBy(route => route.Pattern, StringComparer.Ordinal).ThenBy(route => route.Method, StringComparer.Ordinal).ToArray();
    private static Route[] Protected(Service service) => Routes(service).Where(route => !Infrastructure(route) && !Anonymous(route)).ToArray();

    // The reviewed public surface. A protected endpoint that becomes anonymous is skipped by the matrix below, so the
    // anonymous set itself is pinned: adding or removing an entry is a deliberate, reviewed change.
    private static readonly string[] ReviewedAnonymous =
    [
        "DELETE /api/v1/donors/me (finance)",
        "DELETE /api/v1/donors/me/donations (finance)",
        "DELETE /api/v1/donors/me/identity (finance)",
        "DELETE /api/v1/donors/me/students (sponsorship)",
        "DELETE /api/v1/donors/me/students/{**rest} (sponsorship)",
        "GET /.well-known/jwks.json (auth)",
        "GET /api/v1/campaigns/{slug}/progress (finance)",
        "GET /api/v1/committees (membership)",
        "GET /api/v1/committees/{slug} (membership)",
        "GET /api/v1/content/bootstrap (content)",
        "GET /api/v1/content/calendar-feed/{token}.ics (content)",
        "GET /api/v1/content/consent-withdrawals (content)",
        "GET /api/v1/content/event-feed.ics (content)",
        "GET /api/v1/content/event-occurrences (content)",
        "GET /api/v1/content/event-tickets/{code} (content)",
        "GET /api/v1/content/event-tickets/{code}/calendar.ics (content)",
        "GET /api/v1/content/event-tickets/{code}/certificate (content)",
        "GET /api/v1/content/event-tickets/{code}/feedback-form (content)",
        "GET /api/v1/content/events (content)",
        "GET /api/v1/content/events/{slug} (content)",
        "GET /api/v1/content/events/{slug}/calendar.ics (content)",
        "GET /api/v1/content/events/{slug}/schedule (content)",
        "GET /api/v1/content/faqs (content)",
        "GET /api/v1/content/faqs/{slug} (content)",
        "GET /api/v1/content/items/{type}/{slug} (content)",
        "GET /api/v1/content/news (content)",
        "GET /api/v1/content/news/{id} (content)",
        "GET /api/v1/content/og/{type}/{slug} (content)",
        "GET /api/v1/content/pages (content)",
        "GET /api/v1/content/pages/{slug} (content)",
        "GET /api/v1/content/partner-registry (content)",
        "GET /api/v1/content/partners (content)",
        "GET /api/v1/content/partners/{slug} (content)",
        "GET /api/v1/content/partners/{slug}/details (content)",
        "GET /api/v1/content/press (content)",
        "GET /api/v1/content/press-coverage (content)",
        "GET /api/v1/content/press/{slug} (content)",
        "GET /api/v1/content/press/{slug}/coverage (content)",
        "GET /api/v1/content/preview/{token} (content)",
        "GET /api/v1/content/projects (content)",
        "GET /api/v1/content/projects/{id} (content)",
        "GET /api/v1/content/redirects/resolve (content)",
        "GET /api/v1/content/search (content)",
        "GET /api/v1/content/settings (content)",
        "GET /api/v1/content/success-stories (content)",
        "GET /api/v1/content/success-stories/{slug} (content)",
        "GET /api/v1/content/tags (content)",
        "GET /api/v1/content/team-members (content)",
        "GET /api/v1/content/team-members/{slug} (content)",
        "GET /api/v1/content/testimonials (content)",
        "GET /api/v1/content/testimonials/{slug} (content)",
        "GET /api/v1/donations/presets (finance)",
        "GET /api/v1/donations/webhooks/{gateway} (finance)",
        "GET /api/v1/donations/{reference}/receipt.pdf (finance)",
        "GET /api/v1/donations/{reference}/return (finance)",
        "GET /api/v1/donations/{reference}/status (finance)",
        "GET /api/v1/finance/report-downloads/{id} (finance)",
        "GET /api/v1/members/directory (membership)",
        "GET /api/v1/members/directory/{id}/photo (membership)",
        "GET /api/v1/membership/campaigns/current (membership)",
        "GET /api/v1/membership/privacy (membership)",
        "GET /api/v1/newsletter/preferences/{token} (content)",
        "GET /feed.atom (content)",
        "GET /feed.rss (content)",
        "GET /sitemap.xml (content)",
        "PATCH /api/v1/donors/me (finance)",
        "PATCH /api/v1/donors/me/donations (finance)",
        "PATCH /api/v1/donors/me/identity (finance)",
        "PATCH /api/v1/donors/me/students (sponsorship)",
        "PATCH /api/v1/donors/me/students/{**rest} (sponsorship)",
        "POST /api/v1/auth/confirm-email (auth)",
        "POST /api/v1/auth/forgot-password (auth)",
        "POST /api/v1/auth/login (auth)",
        "POST /api/v1/auth/logout (auth)",
        "POST /api/v1/auth/refresh (auth)",
        "POST /api/v1/auth/register (auth)",
        "POST /api/v1/auth/resend-confirmation (auth)",
        "POST /api/v1/auth/reset-password (auth)",
        "POST /api/v1/content/event-tickets/{code}/accept (content)",
        "POST /api/v1/content/event-tickets/{code}/cancel (content)",
        "POST /api/v1/content/event-tickets/{code}/feedback (content)",
        "POST /api/v1/content/events/{slug}/registrations (content)",
        "POST /api/v1/cta/submissions (cta)",
        "POST /api/v1/donations (finance)",
        "POST /api/v1/donations/webhooks/{gateway} (finance)",
        "POST /api/v1/donations/{reference}/return (finance)",
        "POST /api/v1/donors/me (finance)",
        "POST /api/v1/donors/me/donations (finance)",
        "POST /api/v1/donors/me/identity (finance)",
        "POST /api/v1/donors/me/students (sponsorship)",
        "POST /api/v1/donors/me/students/{**rest} (sponsorship)",
        "POST /api/v1/membership/applications (membership)",
        "POST /api/v1/membership/applications/check (membership)",
        "POST /api/v1/membership/applications/erasure-request (membership)",
        "POST /api/v1/membership/applications/export (membership)",
        "POST /api/v1/membership/applications/status (membership)",
        "POST /api/v1/newsletter/confirm (content)",
        "POST /api/v1/newsletter/provider-events (content)",
        "POST /api/v1/newsletter/subscribe (content)",
        "POST /api/v1/newsletter/unsubscribe/{token} (content)",
        "PUT /api/v1/donors/me (finance)",
        "PUT /api/v1/donors/me/donations (finance)",
        "PUT /api/v1/donors/me/identity (finance)",
        "PUT /api/v1/donors/me/students (sponsorship)",
        "PUT /api/v1/donors/me/students/{**rest} (sponsorship)",
        "PUT /api/v1/membership/applications/{id}/attachments/{kind} (membership)",
        "PUT /api/v1/newsletter/preferences/{token} (content)"
    ];

    [Fact]
    public void EveryGatewayAnonymousOperationAppearsInOpenApi()
    {
        using var reader = File.OpenText(Path.Combine(AppContext.BaseDirectory, "openapi.v1.yaml"));
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        yaml.Load(reader);
        var paths = (YamlDotNet.RepresentationModel.YamlMappingNode)((YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents.Single().RootNode)["paths"];
        var operations = paths.Children.SelectMany(path => ((YamlDotNet.RepresentationModel.YamlMappingNode)path.Value).Children
            .Select(operation => $"{operation.Key.ToString().ToUpperInvariant()} {path.Key}")).ToHashSet(StringComparer.Ordinal);
        // The exporter merges service descriptions and omits /health. API explorer itself omits
        // endpoints marked ExcludeFromDescription, including the read-only donor 405 stubs.
        var missing = Services().SelectMany(Routes)
            .Where(route => ReviewedAnonymous.Contains(route.Label, StringComparer.Ordinal)
                && (route.Pattern.StartsWith("/api/v1", StringComparison.Ordinal) || route.Pattern.StartsWith("/.well-known", StringComparison.Ordinal))
                && route.Endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription != true)
            .Select(route => $"{route.Method} {route.Pattern}").Where(operation => !operations.Contains(operation)).ToArray();
        missing.Should().BeEmpty("gateway anonymous operations must be present in openapi.v1.yaml: {0}", string.Join(", ", missing));
    }

    [Fact]
    public void TheAnonymousSurfaceIsExactlyTheReviewedList()
    {
        var actual = Services().SelectMany(service => Routes(service).Where(route => Anonymous(route) && !Infrastructure(route)))
            .Select(route => route.Label).OrderBy(label => label, StringComparer.Ordinal).ToArray();
        var unexpected = actual.Except(ReviewedAnonymous).ToArray();
        var missing = ReviewedAnonymous.Except(actual).ToArray();
        unexpected.Should().BeEmpty("these endpoints allow anonymous access but are not in the reviewed list:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, unexpected));
        missing.Should().BeEmpty("these reviewed anonymous endpoints are no longer anonymous or no longer exist:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryEndpointExplicitlyDeclaresAuthorizationOrAnonymousAccess()
    {
        var failures = new List<string>();
        foreach (var service in Services())
        {
            var routes = Routes(service);
            routes.Should().NotBeEmpty(service.Name);
            foreach (var route in Protected(service))
            {
                if (route.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0
                    && route.Endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>().Count == 0)
                    failures.Add($"{route.Label} has neither authorization metadata nor AllowAnonymous");
            }
        }
        failures.Should().BeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task EveryProtectedEndpointRejectsMissingMalformedExpiredAndSyntheticIdentityTokens()
    {
        var failures = new List<string>();
        foreach (var service in Services())
        {
            using var client = service.CreateClient();
            foreach (var route in Protected(service))
            {
                await Check(client, route, null, "no token", HttpStatusCode.Unauthorized, failures);
                foreach (var token in TestTokens.Malformed())
                    await Check(client, route, token, "malformed token", HttpStatusCode.Unauthorized, failures);
                await Check(client, route, TestTokens.Expired(route.Permissions), "expired token", HttpStatusCode.Unauthorized, failures);
                if (service.Name == "auth")
                    await Check(client, route, stack.StaffToken(route.Permissions), "synthetic Identity token", HttpStatusCode.Unauthorized, failures);
            }
        }
        failures.Should().BeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task PermissionAndRolePoliciesRejectUnprivilegedCallersAndWritesChangeNoRows()
    {
        await SeedWriteAttempts();
        var low = await stack.NewUserAsync(KjRoles.Volunteer);
        var donor = await stack.NewUserAsync(KjRoles.Donor);
        var failures = new List<string>();
        foreach (var kind in new[] { "donor token", "under-privileged staff token" })
        {
            var before = await Snapshot();
            foreach (var service in Services())
            {
                using var client = service.CreateClient();
                foreach (var route in Protected(service))
                {
                    if (route.Permissions.Length == 0 && !route.DonorSelf)
                    {
                        if (service.Name != "auth") failures.Add($"{route.Label} has no permission policy or DonorSelf policy");
                        continue;
                    }
                    if (kind == "donor token" && route.DonorSelf) continue;
                    var token = service.Name == "auth" ? (kind == "donor token" ? donor.Token : low.Token)
                        : kind == "donor token" ? TestTokens.Issue([], extraClaims: [new Claim(KjClaims.Role, KjRoles.Donor)])
                        : stack.StaffToken(Permissions.All.Except(route.Permissions).ToArray());
                    await Check(client, route, token, kind, HttpStatusCode.Forbidden, failures);
                }
            }
            var after = await Snapshot();
            foreach (var (table, content) in before)
                if (after[table] != content) failures.Add($"{table} content changed after write endpoint pass for {kind}");
            // Session-only auth calls below deliberately revoke sessions, so compare forbidden writes first.
            var auth = Services().Single(service => service.Name == "auth");
            using var identity = auth.CreateClient();
            foreach (var route in Protected(auth).Where(route => route.Permissions.Length == 0 && !route.DonorSelf)
                .OrderBy(route => route.Pattern == "/api/v1/auth/logout-all"))
            {
                var expected = (route.Method, route.Pattern) switch
                {
                    ("GET", "/api/v1/auth/me" or "/api/v1/auth/me/export" or "/api/v1/auth/sessions") => HttpStatusCode.OK,
                    ("GET", "/api/v1/auth/sessions/current") => HttpStatusCode.NoContent,
                    ("POST", "/api/v1/auth/change-password") => HttpStatusCode.BadRequest,
                    ("POST", "/api/v1/auth/logout-all") => HttpStatusCode.NoContent,
                    ("DELETE", "/api/v1/auth/sessions/{id}") => HttpStatusCode.NotFound,
                    _ => throw new InvalidOperationException($"Define the session-only policy expectation for {route.Label}")
                };
                await Check(identity, route, kind == "donor token" ? donor.Token : low.Token, kind, expected, failures);
            }
        }
        failures.Should().BeEmpty(string.Join(Environment.NewLine, failures));
    }

    private async Task<Dictionary<string, string>> Snapshot()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var service in Services())
        {
            using var scope = service.Services.CreateScope();
            var contextType = service.Name switch
            {
                "auth" => typeof(AuthDbContext),
                "content" => typeof(ContentService.Data.ContentDbContext),
                "cta" => typeof(CtaSubmissionService.Data.CtaDbContext),
                "membership" => typeof(MembershipDbContext),
                "finance" => typeof(FinanceDbContext),
                "sponsorship" => typeof(SponsorshipDbContext),
                _ => throw new InvalidOperationException(service.Name)
            };
            var db = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
            foreach (var entity in db.Model.GetEntityTypes().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var task = (Task<string>)typeof(PrivacyEndpointMatrixTests).GetMethod(nameof(TableContent),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .MakeGenericMethod(entity.ClrType).Invoke(null, [db, entity])!;
                result[$"{service.Name}: {entity.Name}"] = await task;
            }
        }
        return result;
    }

    private static async Task<string> TableContent<T>(DbContext db, Microsoft.EntityFrameworkCore.Metadata.IEntityType entity) where T : class
    {
        // No columns are excluded. Tracking exposes shadow scalar properties as well as CLR properties.
        var rows = await db.Set<T>().AsTracking().ToListAsync();
        var properties = entity.GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        return System.Text.Json.JsonSerializer.Serialize(rows.Select(row =>
            System.Text.Json.JsonSerializer.Serialize(properties.ToDictionary(p => p.Name, p => db.Entry(row).Property(p.Name).CurrentValue)))
            .OrderBy(row => row, StringComparer.Ordinal));
    }

    private readonly Dictionary<string, (string Path, object Body, string? Etag)> _writeAttempts = new(StringComparer.Ordinal);

    private async Task SeedWriteAttempts()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await PrivacyFlows.CreateDonor(stack, staff.Token);
        var spare = await PrivacyFlows.CreateDonor(stack, staff.Token);
        var student = await PrivacyFlows.LinkStudent(stack, staff.Token, donor.Id);
        const string donors = "/api/v1/admin/finance/donors";
        const string students = "/api/v1/admin/sponsorship/students";
        async Task Add(string method, string pattern, string path, object body, bool etag = false)
        {
            string? tag = null;
            if (etag)
            {
                using var current = await PrivacyFlows.Send(stack, HttpMethod.Get, path, staff.Token);
                current.EnsureSuccessStatusCode();
                tag = current.Headers.ETag!.Tag;
            }
            _writeAttempts[$"{method} {pattern}"] = (path, body, tag);
        }
        var studentBody = new { name = "Policy probe student", preferredName = "Policy preferred", dateOfBirth = "2011-02-03", school = "Policy probe school", @class = "Class 6", programme = "pathshala", district = "Dhaka", guardianName = "Policy probe guardian", status = "active" };
        using var available = await PrivacyFlows.Send(stack, HttpMethod.Post, students, staff.Token, studentBody);
        var availableStudent = (await PrivacyFlows.Json(available, HttpStatusCode.Created)).GetProperty("id").GetString();
        await Add("POST", students, students, studentBody);
        await Add("PATCH", students + "/{id}", $"{students}/{student}", new { name = "Policy changed student", storyEn = "Policy changed story" }, true);
        await Add("POST", students + "/{id}/consents", $"{students}/{student}/consents", new { granted = false, note = "Policy probe consent note" });
        await Add("POST", donors, donors, new { name = "Policy probe donor", email = "policy-probe@example.org", notes = "Policy probe notes" });
        await Add("PATCH", donors + "/{id}", $"{donors}/{donor.Id}", new { name = "Policy changed donor", notes = "Policy changed notes" }, true);
        await Add("PUT", donors + "/{id}/login/status", $"{donors}/{donor.Id}/login/status", new { status = "disabled" });
        await Add("PUT", donors + "/{id}/login/password", $"{donors}/{donor.Id}/login/password", new { password = "PolicyProbePassword9!" });
        const string links = "/api/v1/admin/sponsorship/donors/{donorId}/students";
        await Add("POST", links, $"/api/v1/admin/sponsorship/donors/{spare.Id}/students", new { studentId = availableStudent, startDate = "2020-01-01" });
        await Add("POST", links + "/{studentId}/unlink", $"/api/v1/admin/sponsorship/donors/{donor.Id}/students/{student}/unlink", new { type = "ended", endDate = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), reason = "Policy probe unlink" });
        const string donations = "/api/v1/admin/finance/donations";
        await Add("POST", donations, donations, new { donorId = donor.Id, amount = "500.00", currency = "BDT", method = "cash", designation = "student", designationId = student });
    }

    private async Task Check(HttpClient client, Route route, string? token, string kind, HttpStatusCode expected, List<string> failures)
    {
        const string placeholder = "x";
        var path = "/" + string.Join('/', route.Endpoint.RoutePattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => parameter.ParameterPolicies.Any(p => p.Content == "int" || p.Content == "long") ? "1"
                : parameter.ParameterPolicies.Any(p => p.Content == "guid") ? "00000000-0000-0000-0000-000000000001" : placeholder,
            _ => throw new InvalidOperationException($"Unsupported route part on {route.Label}")
        }))));
        var attempt = _writeAttempts.GetValueOrDefault($"{route.Method} {route.Pattern}");
        if (attempt.Path is not null) path = attempt.Path;
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), path);
        if (attempt.Etag is not null) request.Headers.TryAddWithoutValidation("If-Match", attempt.Etag);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (route.Method is "POST" or "PUT" or "PATCH") request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(attempt.Body ?? new { }), Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(request);
            if (response.StatusCode != expected)
                failures.Add($"{route.Label} expected {(int)expected} but was {(int)response.StatusCode} for {kind}");
        }
        catch (Exception exception)
        {
            failures.Add($"{route.Label} expected {(int)expected} but threw {exception.GetType().Name}: {exception.Message} for {kind}");
        }
    }
}
