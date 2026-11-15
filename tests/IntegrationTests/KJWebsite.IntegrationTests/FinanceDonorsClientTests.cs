using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SponsorshipService.Links;

namespace KJWebsite.IntegrationTests;

public sealed class FinanceDonorsClientTests
{
    [Theory]
    [InlineData("bn")]
    [InlineData(null)]
    public async Task MeForwardsBearerAndParsesIdentityWithDefaultLanguage(string? language)
    {
        var body = language is null ? "{\"id\":\"dnr_x\"}" : "{\"id\":\"dnr_x\",\"language\":\"bn\"}";
        using var factory = new ScriptedFactory(async request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsoluteUri.Should().Be("http://finance.test/api/v1/donors/me/identity");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("access-token");
            await Task.CompletedTask;
            return Reply(200, body);
        });
        var result = await Client(factory).MeAsync("access-token", default);
        result.Outcome.Should().Be(FinanceOutcome.Ok);
        result.Value.Should().Be(new FinanceMe("dnr_x", language ?? "en"));
    }

    [Theory]
    [InlineData(404, "NOT_A_DONOR", FinanceOutcome.NotFound)]
    [InlineData(403, "DONOR_INACTIVE", FinanceOutcome.Forbidden)]
    [InlineData(403, "ACCOUNT_DISABLED", FinanceOutcome.Forbidden)]
    [InlineData(404, "OTHER", FinanceOutcome.Unavailable)]
    [InlineData(403, "OTHER", FinanceOutcome.Unavailable)]
    [InlineData(404, "DONOR_INACTIVE", FinanceOutcome.Unavailable)]
    [InlineData(403, "NOT_A_DONOR", FinanceOutcome.Unavailable)]
    [InlineData(401, "NOT_A_DONOR", FinanceOutcome.Unavailable)]
    [InlineData(500, "ACCOUNT_DISABLED", FinanceOutcome.Unavailable)]
    public async Task MeOnlyAcceptsRecognizedRefusalCodes(int status, string code, FinanceOutcome expected)
    {
        using var factory = new ScriptedFactory(_ => Task.FromResult(Reply(status, JsonSerializer.Serialize(new { error = new { code } }))));
        var result = await Client(factory).MeAsync("token", default);
        result.Outcome.Should().Be(expected);
        result.Value.Should().BeNull();
        result.Code.Should().Be(expected == FinanceOutcome.Unavailable ? null : code);
    }

    [Theory]
    [InlineData(200, "not JSON")]
    [InlineData(200, "{\"id\":\"\"}")]
    [InlineData(200, "{}")]
    [InlineData(404, "not JSON")]
    [InlineData(403, "not JSON")]
    public async Task MeMalformedResponsesAreUnavailable(int status, string body)
    {
        using var factory = new ScriptedFactory(_ => Task.FromResult(Reply(status, body)));
        (await Client(factory).MeAsync("token", default)).Outcome.Should().Be(FinanceOutcome.Unavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeTransportFailuresAreUnavailable(bool cancelled)
    {
        using var factory = new ScriptedFactory(_ => throw (cancelled ? (Exception)new TaskCanceledException("timeout") : new HttpRequestException("offline")));
        (await Client(factory).MeAsync("token", default)).Outcome.Should().Be(FinanceOutcome.Unavailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("usr_x")]
    public async Task TargetEscapesDonorIdAndAcceptsNullableAccountSubject(string? subject)
    {
        const string id = "donor /?#value";
        using var factory = new ScriptedFactory(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsoluteUri.Should().Be("http://finance.test/api/v1/admin/finance/donors/" + Uri.EscapeDataString(id) + "/link-target");
            request.Headers.Authorization!.Parameter.Should().Be("staff-token");
            return Task.FromResult(Reply(200, JsonSerializer.Serialize(new { id, accountSubject = subject, status = "active", accountStatus = "active" })));
        });
        var result = await Client(factory).TargetAsync(id, "staff-token", default);
        result.Outcome.Should().Be(FinanceOutcome.Ok);
        result.Value.Should().Be(new FinanceTarget(id, subject, "active", "active"));
    }

    [Theory]
    [InlineData(404, "", FinanceOutcome.NotFound)]
    [InlineData(401, "{}", FinanceOutcome.Unavailable)]
    [InlineData(500, "{}", FinanceOutcome.Unavailable)]
    [InlineData(200, "garbage", FinanceOutcome.Unavailable)]
    public async Task TargetMapsRefusalsAndMalformedResponses(int status, string body, FinanceOutcome expected)
    {
        using var factory = new ScriptedFactory(_ => Task.FromResult(Reply(status, body)));
        (await Client(factory).TargetAsync("dnr_x", "token", default)).Outcome.Should().Be(expected);
    }

    [Theory]
    [InlineData(200, "queued", FinanceOutcome.Ok)]
    [InlineData(200, "suppressed", FinanceOutcome.Ok)]
    [InlineData(200, "unexpected", FinanceOutcome.Unavailable)]
    [InlineData(201, "queued", FinanceOutcome.Unavailable)]
    [InlineData(404, "queued", FinanceOutcome.NotFound)]
    [InlineData(500, "queued", FinanceOutcome.Unavailable)]
    public async Task NotifySendsIdempotentLinkBodyAndParsesOnlyKnownOutcomes(int status, string outcome, FinanceOutcome expected)
    {
        using var factory = new ScriptedFactory(async request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsoluteUri.Should().Be("http://finance.test/api/v1/admin/finance/donors/dnr_x/link-notices");
            request.Headers.Authorization!.Parameter.Should().Be("staff-token");
            request.Headers.GetValues("Idempotency-Key").Should().Equal("lnk_x");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            body.RootElement.GetProperty("linkId").GetString().Should().Be("lnk_x");
            return Reply(status, JsonSerializer.Serialize(new { outcome }));
        });
        var result = await Client(factory).NotifyAsync("dnr_x", "lnk_x", "staff-token", default);
        result.Outcome.Should().Be(expected);
        result.Value.Should().Be(expected == FinanceOutcome.Ok ? outcome : null);
    }

    [Theory]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: links.StudentId'.", true)]
    [InlineData("UNIQUE constraint failed: links.DonorId, links.StudentId", true)]
    [InlineData("23505: duplicate key value violates unique constraint \"UX_links_active_student\"", true)]
    [InlineData("23505: duplicate key value violates unique constraint \"UX_links_active_pair\"", true)]
    [InlineData("23505: duplicate key value violates unique constraint \"UX_other\"", false)]
    [InlineData("UNIQUE constraint failed: students.Id", false)]
    [InlineData("FOREIGN KEY constraint failed", false)]
    public void ActiveUniqueViolationRecognizesOnlyActiveLinkConstraints(string message, bool expected)
    {
        LinkEndpoints.ActiveUniqueViolation(new DbUpdateException("Save failed", new InvalidOperationException(message))).Should().Be(expected);
    }

    private static FinanceDonors Client(IHttpClientFactory factory) => new(factory, NullLogger<FinanceDonors>.Instance);
    private static HttpResponseMessage Reply(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class ScriptedFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;
        public ScriptedFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> script) =>
            _client = new(new ScriptedHandler(script)) { BaseAddress = new("http://finance.test/") };
        public HttpClient CreateClient(string name)
        {
            name.Should().Be("finance");
            return _client;
        }
        public void Dispose() => _client.Dispose();
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> script) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => script(request);
    }
}
