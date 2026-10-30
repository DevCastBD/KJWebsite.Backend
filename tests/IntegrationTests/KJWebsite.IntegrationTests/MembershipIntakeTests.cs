using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MembershipService.Data;
using MembershipService.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class MembershipIntakeTests : IDisposable
{
    private const string Applications = "/api/v1/membership/applications";
    private const string Email = "unique.applicant.7731@example.org";
    private const string Phone = "01977887087";
    private const string Address = "69/E Zigatola Lane, Dhanmondi";

    private readonly MembershipApiFactory _factory = new();
    private readonly HttpClient _client;

    public MembershipIntakeTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task HealthReturnsOk()
    {
        using var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ValidApplicationIsCreatedWithAReference()
    {
        using var first = await Post(Valid());
        using var second = await Post(Valid(email: "other@example.org", phone: "01711111111"));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await Json(first);
        body.GetProperty("id").GetString().Should().StartWith("app_");
        body.GetProperty("reference").GetString().Should().MatchRegex($"^KJ-{DateTime.UtcNow.Year}-[0-9A-HJKMNP-TV-Z]{{8}}$");
        body.GetProperty("status").GetString().Should().Be("received");
        (await Json(second)).GetProperty("reference").GetString().Should().NotBe(body.GetProperty("reference").GetString());
    }

    [Fact]
    public async Task EveryLegacyFieldReachesTheStore()
    {
        using var response = await Post(Valid() with
        {
            Gender = "female",
            DateOfBirth = "1998-04-12",
            CityOfResidence = "Dhaka",
            CountryOfResidence = "Bangladesh"
        });
        var id = (await Json(response)).GetProperty("id").GetString()!;

        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Applications.SingleAsync(a => a.Id == id);
        var protector = scope.ServiceProvider.GetRequiredService<IFieldProtector>();

        row.FirstName.Should().Be("Tahmid");
        row.LastName.Should().Be("Rahman");
        row.AcademicInstitute.Should().Be("Dhaka University");
        row.Occupation.Should().Be("Student");
        row.ReasonForJoining.Should().Be("I want to help.");
        row.VolunteeringExperience.Should().Be("2 years");
        row.PresentOrganization.Should().Be("Amra Korbo Joy");
        row.HeardAboutUs.Should().Be("Facebook");
        row.Gender.Should().Be("female");
        row.CityOfResidence.Should().Be("Dhaka");
        row.CountryOfResidence.Should().Be("Bangladesh");
        row.Language.Should().Be("en");
        protector.Unprotect(row.EmailProtected).Should().Be(Email);
        protector.Unprotect(row.PhoneProtected).Should().Be(Phone);
        protector.Unprotect(row.BloodGroupProtected).Should().Be("O+");
        protector.Unprotect(row.CurrentAddressProtected).Should().Be(Address);
        protector.Unprotect(row.DateOfBirthProtected!).Should().Be("1998-04-12");
    }

    [Fact]
    public async Task SensitiveFieldsAreNotStoredInThePlain()
    {
        using var response = await Post(Valid());
        var id = (await Json(response)).GetProperty("id").GetString()!;

        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Applications.SingleAsync(a => a.Id == id);
        var stored = string.Join('|', row.EmailProtected, row.PhoneProtected, row.BloodGroupProtected, row.CurrentAddressProtected, row.EmailHash, row.PhoneHash);

        stored.Should().NotContain(Email).And.NotContain(Phone).And.NotContain("Zigatola");
    }

    [Theory]
    [InlineData("firstName")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("bloodGroup")]
    [InlineData("currentAddress")]
    [InlineData("occupation")]
    [InlineData("language")]
    public async Task MissingRequiredFieldsAreRejectedByName(string field)
    {
        var payload = JsonSerializer.SerializeToNode(Valid(), JsonSerializerOptions.Web)!.AsObject();
        payload.Remove(field);

        using var response = await _client.PostAsJsonAsync(Applications, payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(field);
    }

    [Fact]
    public async Task MalformedValuesAreRejectedWithoutEchoingThem()
    {
        using var response = await Post(Valid(email: "not-an-email", phone: "12") with { BloodGroup = "Z+", DateOfBirth = "2999-01-01" });
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Contain("email").And.Contain("phone").And.Contain("bloodGroup").And.Contain("dateOfBirth");
        text.Should().NotContain("not-an-email").And.NotContain("2999");
    }

    [Fact]
    public async Task DuplicateCheckWarnsByEmailOrPhoneAndNeverBlocksASubmission()
    {
        (await Check("fresh@example.org", "01600000000")).Should().BeFalse();
        (await Post(Valid())).StatusCode.Should().Be(HttpStatusCode.Created);

        (await Check(Email.ToUpperInvariant(), "01600000000")).Should().BeTrue("same email, different case");
        (await Check("fresh@example.org", "+880 1977-887087")).Should().BeTrue("same phone, different format");
        (await Check("fresh@example.org", "01600000000")).Should().BeFalse();
        (await Post(Valid())).StatusCode.Should().Be(HttpStatusCode.Created, "re-applying is legitimate");
    }

    [Fact]
    public async Task DuplicateCheckRejectsMalformedInput()
    {
        using var response = await _client.PostAsJsonAsync($"{Applications}/check", new { email = "nope", phone = "1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReplayingAnIdempotencyKeyCreatesOneApplication()
    {
        var key = Guid.NewGuid().ToString();

        var payload = Valid();
        using var first = await Post(payload, key);
        using var second = await Post(payload, key);

        second.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
        (await Json(second)).GetProperty("id").GetString().Should().Be((await Json(first)).GetProperty("id").GetString());
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Applications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AttachmentsAreCheckedStoredAndMarkedPending()
    {
        var id = await CreateApplication();

        using var cv = await Put(id, "cv", "application/pdf", Pdf(), "My%20CV%20%C3%A9.pdf");
        using var photo = await Put(id, "photo", "image/png", Png(), "me.png");

        cv.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(cv);
        body.GetProperty("scanStatus").GetString().Should().Be("pending");
        body.GetProperty("fileName").GetString().Should().Be("My CV é.pdf");
        photo.StatusCode.Should().Be(HttpStatusCode.OK);
        Directory.GetFiles(_factory.AttachmentsPath).Should().HaveCount(2);
    }

    [Fact]
    public async Task ReuploadingReplacesTheFile()
    {
        var id = await CreateApplication();
        using var first = await Put(id, "cv", "application/pdf", Pdf(), "a.pdf");
        using var second = await Put(id, "cv", "application/pdf", Pdf(40), "b.pdf");

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        Directory.GetFiles(_factory.AttachmentsPath).Should().HaveCount(1);
        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MembershipDbContext>().Attachments.ToListAsync();
        rows.Should().ContainSingle().Which.FileName.Should().Be("b.pdf");
    }

    [Fact]
    public async Task AttachmentContentMustMatchItsDeclaredType()
    {
        var id = await CreateApplication();

        using var fake = await Put(id, "cv", "application/pdf", Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "cv.pdf");
        using var wrongType = await Put(id, "cv", "text/html", Pdf(), "cv.html");
        using var empty = await Put(id, "photo", "image/png", [], "p.png");

        fake.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        wrongType.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        empty.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        Directory.Exists(_factory.AttachmentsPath).Should().BeFalse("nothing was stored");
    }

    [Fact]
    public async Task OversizedAttachmentsAreRejected()
    {
        var id = await CreateApplication();

        using var photo = await Put(id, "photo", "image/png", Png(2 * 1024 * 1024 + 1), "big.png");
        using var cv = await Put(id, "cv", "application/pdf", Pdf(5 * 1024 * 1024 + 1), "big.pdf");

        photo.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        cv.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task AttachmentsNeedAKnownKindAndApplication()
    {
        var id = await CreateApplication();

        (await Put(id, "passport", "application/pdf", Pdf(), "x.pdf")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Put("app_missing", "cv", "application/pdf", Pdf(), "x.pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PersonalDataNeverAppearsInLogs()
    {
        var id = await CreateApplication();
        await Check(Email, Phone);
        await Put(id, "cv", "application/pdf", Pdf(), "private-name.pdf");
        await Post(Valid(email: "bad", phone: "1"));

        var logs = string.Join('\n', _factory.Logs.Lines);

        logs.Should().NotBeEmpty();
        logs.Should().NotContain(Email).And.NotContain(Phone).And.NotContain("Zigatola").And.NotContain("Tahmid").And.NotContain("private-name");
    }

    [Theory]
    [InlineData("+880 1977-887087", "01977887087")]
    [InlineData("01977887087", "1977887087")]
    public void PhoneNormalisationMatchesLocalAndInternationalForms(string a, string b) =>
        ContactHasher.NormalizePhone(a).Should().Be(ContactHasher.NormalizePhone(b));

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static byte[] Pdf(int size = 64) => Fill("%PDF-1.7\n"u8, size);

    private static byte[] Png(int size = 64) => Fill([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], size);

    private static byte[] Fill(ReadOnlySpan<byte> head, int size)
    {
        var bytes = new byte[Math.Max(size, head.Length)];
        head.CopyTo(bytes);
        return bytes;
    }

    private static Payload Valid(string email = Email, string phone = Phone) => new(
        "Tahmid", "Rahman", email, phone, "O+", Address, "Dhaka University", "Student",
        "I want to help.", "2 years", "Amra Korbo Joy", "Facebook", null, null, null, null, "en", DateTimeOffset.UtcNow);

    private async Task<string> CreateApplication()
    {
        using var response = await Post(Valid());
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> Post(Payload payload, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Applications) { Content = JsonContent.Create(payload) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await _client.SendAsync(request);
    }

    private async Task<bool> Check(string email, string phone)
    {
        using var response = await _client.PostAsJsonAsync($"{Applications}/check", new { email, phone });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(response)).GetProperty("duplicate").GetBoolean();
    }

    private async Task<HttpResponseMessage> Put(string id, string kind, string contentType, byte[] body, string fileName)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Applications}/{id}/attachments/{kind}") { Content = content };
        request.Headers.Add("X-Filename", fileName);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private sealed record Payload(
        string FirstName, string LastName, string Email, string Phone, string BloodGroup, string CurrentAddress,
        string AcademicInstitute, string Occupation, string? ReasonForJoining, string? VolunteeringExperience,
        string? PresentOrganization, string? HeardAboutUs, string? Gender, string? DateOfBirth,
        string? CityOfResidence, string? CountryOfResidence, string Language, DateTimeOffset SubmittedAt);
}
