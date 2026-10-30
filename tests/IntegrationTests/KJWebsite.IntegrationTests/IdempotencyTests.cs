using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CtaSubmissionService.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class IdempotencyTests : IDisposable
{
    private const string Path = "/api/v1/cta/submissions";
    private readonly CtaApiFactory _factory = new();
    private readonly HttpClient _client;

    public IdempotencyTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task ReplayingAKeyReturnsTheOriginalResponseAndCreatesNothingNew()
    {
        var before = SubmissionCount();
        var key = Guid.NewGuid().ToString();

        using var first = await Post(key, "Rahim");
        using var second = await Post(key, "Rahim");

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Contains("Idempotency-Replayed").Should().BeFalse();
        second.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
        second.Headers.Location.Should().Be(first.Headers.Location);
        (await Id(second)).Should().Be(await Id(first));
        SubmissionCount().Should().Be(before + 1);
    }

    [Fact]
    public async Task RequestsWithoutAKeyAreNeverDeduplicated()
    {
        var before = SubmissionCount();

        (await Post(null, "Rahim")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Post(null, "Rahim")).StatusCode.Should().Be(HttpStatusCode.Created);

        SubmissionCount().Should().Be(before + 2);
    }

    [Fact]
    public async Task ReusingAKeyWithADifferentBodyIsRejected()
    {
        var key = Guid.NewGuid().ToString();
        (await Post(key, "Rahim")).StatusCode.Should().Be(HttpStatusCode.Created);

        using var reused = await Post(key, "Karim");

        reused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await reused.Content.ReadAsStringAsync()).Should().Contain("IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task ConcurrentDuplicatesCreateExactlyOneSubmission()
    {
        var before = SubmissionCount();
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Post(key, "Rahim")));

        responses.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.Created || s == HttpStatusCode.Conflict);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created && !r.Headers.Contains("Idempotency-Replayed")).Should().Be(1);
        SubmissionCount().Should().Be(before + 1);
    }

    [Fact]
    public async Task AFailedAttemptCanBeRetriedWithTheSameKey()
    {
        var key = Guid.NewGuid().ToString();
        using var invalid = await _client.SendAsync(Request(key, new { formName = "nope", ctaType = "contact", language = "en", sourcePath = "/", values = new { }, submittedAt = DateTimeOffset.UtcNow }));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var corrected = await Post(key, "Rahim");

        corrected.StatusCode.Should().Be(HttpStatusCode.Created);
        corrected.Headers.Contains("Idempotency-Replayed").Should().BeFalse("the 400 was not remembered");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("বাংলা")]
    public async Task AMalformedKeyIsRejected(string key)
    {
        using var request = Request("x", Body("Rahim"));
        request.Headers.Remove("Idempotency-Key");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheKeyIsDocumentedInTheOpenApiContract()
    {
        using var openApi = JsonDocument.Parse(await _client.GetStringAsync("/openapi/v1.json"));

        openApi.RootElement.GetProperty("paths").GetProperty(Path).GetProperty("post").GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()).Should().Contain("Idempotency-Key");
    }

    private static object Body(string firstName) => new
    {
        formName = "contact",
        ctaType = "contact",
        language = "en",
        sourcePath = "/contact",
        values = new Dictionary<string, object?> { ["firstName"] = firstName },
        submittedAt = DateTimeOffset.UtcNow
    };

    private static HttpRequestMessage Request(string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private async Task<HttpResponseMessage> Post(string? key, string firstName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(JsonSerializer.Serialize(BodyFor(firstName)), System.Text.Encoding.UTF8, "application/json") };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await _client.SendAsync(request);
    }

    // Fixed timestamp so a replay's body is byte-identical to the original.
    private static object BodyFor(string firstName) => new
    {
        formName = "contact",
        ctaType = "contact",
        language = "en",
        sourcePath = "/contact",
        values = new Dictionary<string, object?> { ["firstName"] = firstName },
        submittedAt = "2026-10-31T00:00:00Z"
    };

    private static async Task<string?> Id(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

    private int SubmissionCount()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CtaDbContext>().Submissions.Count();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
