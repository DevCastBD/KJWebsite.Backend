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
    private static bool Infrastructure(Route route) => new[] { "/health", "/openapi", "/swagger" }
        .Any(prefix => route.Pattern.Equals(prefix, StringComparison.OrdinalIgnoreCase) || route.Pattern.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    private static bool Anonymous(Route route) => route.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
    private static Route[] Routes(Service service) => service.Services.GetRequiredService<EndpointDataSource>().Endpoints
        .OfType<RouteEndpoint>()
        .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            .Select(method => new Route(service, endpoint, method)))
        .OrderBy(route => route.Pattern, StringComparer.Ordinal).ThenBy(route => route.Method, StringComparer.Ordinal).ToArray();
    private static Route[] Protected(Service service) => Routes(service).Where(route => !Infrastructure(route) && !Anonymous(route)).ToArray();

    [Fact]
    public void EveryEndpointExplicitlyDeclaresAuthorizationOrAnonymousAccess()
    {
        var failures = new List<string>();
        foreach (var service in Services())
        {
            var routes = Routes(service);
            routes.Should().NotBeEmpty(service.Name);
            Console.WriteLine($"{service.Name}: {Protected(service).Length} protected, {routes.Count(Anonymous)} anonymous, {routes.Count(Infrastructure)} infrastructure");
            foreach (var route in routes.Where(Anonymous)) Console.WriteLine($"Anonymous: {route.Label}");
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
                    if (route.Permissions.Length == 0 && !route.DonorSelf) continue;
                    if (kind == "donor token" && route.DonorSelf) continue;
                    var token = service.Name == "auth" ? (kind == "donor token" ? donor.Token : low.Token)
                        : kind == "donor token" ? TestTokens.Issue([], extraClaims: [new Claim(KjClaims.Role, KjRoles.Donor)])
                        : stack.StaffToken(Permissions.All.Except(route.Permissions).ToArray());
                    await Check(client, route, token, kind, HttpStatusCode.Forbidden, failures);
                }
            }
            var auth = Services().Single(service => service.Name == "auth");
            using var identity = auth.CreateClient();
            foreach (var route in Protected(auth).Where(route => route.Permissions.Length == 0 && !route.DonorSelf)
                .OrderBy(route => route.Pattern == "/api/v1/auth/logout-all"))
            {
                var expected = (route.Method, route.Pattern) switch
                {
                    ("GET", "/api/v1/auth/me" or "/api/v1/auth/me/export" or "/api/v1/auth/sessions") => HttpStatusCode.OK,
                    ("POST", "/api/v1/auth/change-password") => HttpStatusCode.BadRequest,
                    ("POST", "/api/v1/auth/logout-all") => HttpStatusCode.NoContent,
                    ("DELETE", "/api/v1/auth/sessions/{id}") => HttpStatusCode.NotFound,
                    _ => throw new InvalidOperationException($"Define the session-only policy expectation for {route.Label}")
                };
                await Check(identity, route, kind == "donor token" ? donor.Token : low.Token, kind, expected, failures);
            }
            var after = await Snapshot();
            foreach (var (table, count) in before)
                if (after[table] != count) failures.Add($"{table} changed from {count} to {after[table]} after write endpoint pass for {kind}");
        }
        failures.Should().BeEmpty(string.Join(Environment.NewLine, failures));
    }

    private async Task<Dictionary<string, int>> Snapshot()
    {
        using var sponsorship = stack.Sponsorship.Services.CreateScope();
        using var finance = stack.Finance.Services.CreateScope();
        using var auth = stack.Auth.Services.CreateScope();
        using var membership = stack.Membership.Services.CreateScope();
        var students = sponsorship.ServiceProvider.GetRequiredService<SponsorshipDbContext>();
        var gifts = finance.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var users = auth.ServiceProvider.GetRequiredService<AuthDbContext>();
        var applications = membership.ServiceProvider.GetRequiredService<MembershipDbContext>();
        return new()
        {
            ["sponsorship students"] = await students.Students.CountAsync(),
            ["sponsorship links"] = await students.Links.CountAsync(),
            ["finance donors"] = await gifts.Donors.CountAsync(),
            ["finance donations"] = await gifts.Donations.CountAsync(),
            ["donor logins"] = await users.UserRoles.CountAsync(role => users.Roles.Any(r => r.Id == role.RoleId && r.Name == KjRoles.Donor)),
            ["membership applications"] = await applications.Applications.CountAsync()
        };
    }

    private static async Task Check(HttpClient client, Route route, string? token, string kind, HttpStatusCode expected, List<string> failures)
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
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), path);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (route.Method is "POST" or "PUT" or "PATCH") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
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
