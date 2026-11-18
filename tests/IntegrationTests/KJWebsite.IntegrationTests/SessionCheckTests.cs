using System.Net;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KJWebsite.IntegrationTests;

public sealed class SessionCheckTests
{
    [Theory]
    [InlineData("204", 200)]
    [InlineData("401", 401)]
    [InlineData("403", 401)]
    [InlineData("500", 503)]
    [InlineData("404", 503)]
    [InlineData("throw", 503)]
    [InlineData("timeout", 503)]
    [InlineData("cancel", 503)]
    public async Task ProtectedEndpointFailsClosed(string result, int expected)
    {
        using var handler = new StubHandler(result);
        await using var app = await Host(handler, timeout: result == "timeout" ? "00:00:00.025" : null);
        using var client = app.GetTestClient();
        using var response = await Send(client, "/protected", TestTokens.Issue([]));
        ((int)response.StatusCode).Should().Be(expected);
        handler.Calls.Should().Be(1);
        if (expected == 503) (await response.Content.ReadAsStringAsync()).Should().Contain("AUTH_UNAVAILABLE");
    }

    [Fact]
    public async Task DisabledCheckDoesNotCallIdentity()
    {
        using var handler = new StubHandler("throw");
        await using var app = await Host(handler, enabled: false, development: true);
        using var client = app.GetTestClient();
        using var response = await Send(client, "/protected", TestTokens.Issue([]));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DisablingTheCheckIsIgnoredOutsideDevelopment()
    {
        using var handler = new StubHandler("401");
        await using var app = await Host(handler, enabled: false);
        using var client = app.GetTestClient();
        using var response = await Send(client, "/protected", TestTokens.Issue([]));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ARedirectAnswerFromIdentityFailsClosedAsUnavailable()
    {
        using var handler = new StubHandler("302");
        await using var app = await Host(handler);
        using var client = app.GetTestClient();
        using var response = await Send(client, "/protected", TestTokens.Issue([]));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ConfigurationIsReadForEachRequest()
    {
        using var handler = new StubHandler("401");
        await using var app = await Host(handler, enabled: false, development: true);
        using var client = app.GetTestClient();
        var token = TestTokens.Issue([]);
        using var before = await Send(client, "/protected", token);
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Services.GetRequiredService<IConfiguration>()["Auth:SessionCheck:Enabled"] = "true";
        using var after = await Send(client, "/protected", token);
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AbortedRequestFailsWithoutUnavailableFlagOrChallengeBody()
    {
        using var handler = new StubHandler("throw");
        await using var app = await Host(handler);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var http = new DefaultHttpContext { RequestServices = app.Services, RequestAborted = aborted.Token };
        http.Request.Headers.Authorization = "Bearer " + TestTokens.Issue([]);
        using var body = new MemoryStream();
        http.Response.Body = body;
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var validated = new TokenValidatedContext(http, scheme, options);
        await SessionCheck.ValidateAsync(validated);
        validated.Result!.Failure.Should().NotBeNull();
        http.Items.Should().NotContainKey(SessionCheck.UnavailableKey);
        var challenge = new JwtBearerChallengeContext(http, scheme, options, new AuthenticationProperties());
        await options.Events.Challenge(challenge);
        body.Length.Should().Be(0);
        http.Response.StatusCode.Should().NotBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("invalid")]
    [InlineData("expired")]
    [InlineData("forged")]
    public async Task UnvalidatedTokensNeverReachIdentity(string tokenKind)
    {
        using var handler = new StubHandler("204");
        await using var app = await Host(handler);
        using var client = app.GetTestClient();
        var token = tokenKind switch
        {
            "none" => null,
            "expired" => TestTokens.Expired([]),
            "forged" => TestTokens.Issue([], TestJwtKeys.Other),
            _ => "not-a-jwt"
        };
        using var response = await Send(client, "/protected", token);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        handler.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("401")]
    [InlineData("throw")]
    public async Task AnonymousEndpointStillAnswersWhenAuthenticationFails(string result)
    {
        using var handler = new StubHandler(result);
        await using var app = await Host(handler);
        using var client = app.GetTestClient();
        using var response = await Send(client, "/anonymous", TestTokens.Issue([]));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ForwardsExactAuthorizationToConfiguredIdentityWithoutBody()
    {
        using var handler = new StubHandler("204");
        await using var app = await Host(handler, address: "http://identity.test:8123/ignored/");
        using var client = app.GetTestClient();
        var token = TestTokens.Issue([]);
        using var response = await Send(client, "/protected", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Authorization.Should().Be("bEaReR " + token);
        handler.Uri.Should().Be(new Uri("http://identity.test:8123/api/v1/auth/sessions/current"));
        handler.Method.Should().Be(HttpMethod.Get);
        handler.HadBody.Should().BeFalse();
    }

    [Fact]
    public async Task MalformedAddressFailsClosed()
    {
        using var handler = new StubHandler("204");
        await using var app = await Host(handler, address: "not a URL");
        using var client = app.GetTestClient();
        using var response = await Send(client, "/protected", TestTokens.Issue([]));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AUTH_UNAVAILABLE");
        handler.Calls.Should().Be(0);
    }

    private static async Task<WebApplication> Host(StubHandler handler, bool enabled = true, string? address = null, string? timeout = null, bool development = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = development ? Environments.Development : Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary),
            ["Auth:SessionCheck:Timeout"] = timeout ?? "00:00:05",
            ["Services:Auth:Url"] = address ?? "http://localhost:7003/"
        });
        // Leave Enabled unset in the ordinary cases to exercise the production default.
        if (!enabled) builder.Configuration["Auth:SessionCheck:Enabled"] = "false";
        builder.Services.AddKjJwtAuthentication();
        builder.Services.AddHttpClient(SessionCheck.ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/protected", () => Results.Ok()).RequireAuthorization();
        app.MapGet("/anonymous", () => Results.Ok()).AllowAnonymous();
        await app.StartAsync();
        return app;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", "bEaReR " + token);
        return client.SendAsync(request);
    }

    private sealed class StubHandler(string result) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public bool HadBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.GetValues("Authorization").Single();
            Uri = request.RequestUri;
            Method = request.Method;
            HadBody = request.Content is not null;
            if (result == "throw") throw new HttpRequestException("Unavailable");
            if (result == "cancel") throw new OperationCanceledException();
            if (result == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage((HttpStatusCode)int.Parse(result, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
