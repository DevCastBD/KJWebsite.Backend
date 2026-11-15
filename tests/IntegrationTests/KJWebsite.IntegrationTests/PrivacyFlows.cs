using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

internal static class PrivacyFlows
{
    private static int _studentSeed;

    public static async Task UploadPhoto(PrivacyStack stack, string token, string studentId)
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
            .. Encoding.ASCII.GetBytes("privacy photo pixels"), 0xFF, 0xD9];
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new("image/jpeg");
        form.Add(file, "photo", "photo.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Students}/{studentId}/photo") { Content = form };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await stack.Gateway.SendAsync(request);
        await Json(response, HttpStatusCode.OK);
    }

    private const string Donors = "/api/v1/admin/finance/donors";
    private const string Students = "/api/v1/admin/sponsorship/students";
    public static async Task<HttpResponseMessage> Send(PrivacyStack stack, HttpMethod method, string path, string? token = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (method == HttpMethod.Post) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await stack.Gateway.SendAsync(request);
    }
    public static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    public static async Task<(string Id, string Email, string Password)> CreateDonor(PrivacyStack stack, string token, Action<string, string>? capture = null)
    {
        var name = stack.NewSeed("donor");
        var email = stack.Seed("donor email", $"{stack.NewSeed("email")}@example.org");
        var password = stack.NewSeed("Password9");
        using var response = await Send(stack, HttpMethod.Post, Donors, token, new { name, email });
        capture?.Invoke($"POST {Donors}", await response.Content.ReadAsStringAsync());
        var id = (await Json(response, HttpStatusCode.Created)).GetProperty("donor").GetProperty("id").GetString()!;
        using var provision = await Send(stack, HttpMethod.Post, $"{Donors}/{id}/login", token, new { password });
        capture?.Invoke($"POST {Donors}/{id}/login", await provision.Content.ReadAsStringAsync());
        await Json(provision, HttpStatusCode.Created);
        return (id, email, password);
    }
    public static async Task<string> LinkStudent(PrivacyStack stack, string token, string donorId)
    {
        // A process-wide ordinal makes both values unique across fixtures without random collisions.
        var seed = Interlocked.Increment(ref _studentSeed);
        var birth = new DateOnly(2010, 1, 1).AddDays(seed).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var guardianPhone = stack.Seed("guardian phone", $"+88017{seed:D8}");
        using var created = await Send(stack, HttpMethod.Post, Students, token, new
        {
            name = stack.NewSeed("student"),
            preferredName = stack.NewSeed("preferred"),
            dateOfBirth = birth,
            gender = "female",
            school = stack.NewSeed("school"),
            @class = "Class 6",
            programme = "pathshala",
            district = "Dhaka",
            address = stack.NewSeed("address"),
            guardianName = stack.NewSeed("guardian"),
            guardianPhone,
            status = "active"
        });
        var id = (await Json(created, HttpStatusCode.Created)).GetProperty("id").GetString()!;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4 signed form"));
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "form", "form.pdf");
        using var upload = new HttpRequestMessage(HttpMethod.Put, $"{Students}/{id}/consent-form") { Content = form };
        upload.Headers.Authorization = new("Bearer", token);
        using var uploaded = await stack.Gateway.SendAsync(upload);
        await Json(uploaded, HttpStatusCode.OK);
        using var consent = await Send(stack, HttpMethod.Post, $"{Students}/{id}/consents", token, new { granted = true });
        await Json(consent, HttpStatusCode.Created);
        using var link = await Send(stack, HttpMethod.Post, $"/api/v1/admin/sponsorship/donors/{donorId}/students", token,
            new { studentId = id, startDate = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        var linked = await Json(link, HttpStatusCode.Created);
        var linkId = linked.GetProperty("id").GetString();
        using var scope = stack.Sponsorship.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<SponsorshipService.Data.SponsorshipDbContext>().Links.SingleAsync(l => l.Id == linkId);
        row.NoticeStatus.Should().Be("suppressed");
        return id;
    }
    public static void Private(HttpResponseMessage response)
    {
        response.Headers.CacheControl.Should().BeEquivalentTo(System.Net.Http.Headers.CacheControlHeaderValue.Parse("private, no-store"));
        response.Headers.Vary.Should().Contain("Authorization");
    }
}
