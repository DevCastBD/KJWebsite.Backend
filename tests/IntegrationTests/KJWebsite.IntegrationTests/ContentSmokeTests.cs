using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace KJWebsite.IntegrationTests;

public sealed class ContentSmokeTests : IDisposable
{
    private readonly ContentApiFactory _factory = new();
    private readonly HttpClient _client;

    public ContentSmokeTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task HealthReturnsOk()
    {
        using var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListProjectsReturnsOk()
    {
        using var response = await _client.GetAsync("/api/v1/content/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetProjectReturnsOk()
    {
        using var response = await _client.GetAsync("/api/v1/content/projects/pathshala");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListNewsReturnsOk()
    {
        using var response = await _client.GetAsync("/api/v1/content/news");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetNewsReturnsOk()
    {
        using var response = await _client.GetAsync("/api/v1/content/news/bauniabadh-mou");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateProjectReturnsCreated()
    {
        using var response = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/content/projects", ProjectPayload("new-project"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UpdateProjectReturnsOk()
    {
        using var response = await SendAdminAsync(HttpMethod.Put, "/api/v1/admin/content/projects/pathshala", ProjectPayload("pathshala"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteProjectReturnsNoContent()
    {
        using var request = AuthorizedRequest(HttpMethod.Delete, "/api/v1/admin/content/projects/pathshala");
        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CreateNewsReturnsCreated()
    {
        using var response = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/content/news", NewsPayload("new-news"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UpdateNewsReturnsOk()
    {
        using var response = await SendAdminAsync(HttpMethod.Put, "/api/v1/admin/content/news/bauniabadh-mou", NewsPayload("bauniabadh-mou"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteNewsReturnsNoContent()
    {
        using var request = AuthorizedRequest(HttpMethod.Delete, "/api/v1/admin/content/news/bauniabadh-mou");
        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, object payload)
    {
        using var request = AuthorizedRequest(method, path);
        request.Content = JsonContent.Create(payload);
        return await _client.SendAsync(request);
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string path) => new(method, path)
    {
        Headers = { Authorization = new("Bearer", "dev-admin-token") }
    };

    private static object ProjectPayload(string slug) => new
    {
        slug,
        status = "active",
        translations = new[] { new { lang = "en", title = "Integration Test Project" } }
    };

    private static object NewsPayload(string slug) => new
    {
        slug,
        status = "published",
        translations = new[] { new { lang = "en", title = "Integration Test News" } }
    };
}
