using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SponsorshipService.Data;
using SponsorshipService.Students;

namespace KJWebsite.IntegrationTests;

/// <summary>Sponsorship with a throwaway file folder and a fixed project list in place of the Content service.</summary>
public sealed class SponsorshipApiFactory : SqliteWebApplicationFactory<SponsorshipService.SponsorshipServiceTestEntryPoint, SponsorshipDbContext>
{
    public string FilesPath { get; } = Path.Combine(Path.GetTempPath(), $"kj-test-sponsorship-{Guid.NewGuid():N}");

    public FakeProjects Projects { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sponsorship:FilesPath"] = FilesPath,
                ["Jwt:PublicKeyPem"] = TestTokens.PublicKeyPem(TestJwtKeys.Primary)
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IProjectCatalogue>();
            services.AddSingleton<IProjectCatalogue>(Projects);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(FilesPath))
        {
            Directory.Delete(FilesPath, recursive: true);
        }
    }
}

public sealed class FakeProjects : IProjectCatalogue
{
    private static readonly ProgrammeRef[] Known = [new("pathshala", "Pathshala"), new("stipend", "Stipend Fund")];

    /// <summary>Simulates Content being unreachable.</summary>
    public bool Down { get; set; }

    public Task<IReadOnlyList<ProgrammeRef>?> ListAsync(string language, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProgrammeRef>?>(Down ? null : Known);

    public Task<bool?> ExistsAsync(string slug, CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(Down ? null : Known.Any(p => p.Id == slug));
}

/// <summary>
/// Students, guardian consent, photos and signed forms (BE-106, #213). High blast radius: children's personal data, files and consent, so
/// the tests lean on who may NOT read what, on what is kept out of the log, and on the order of the consent rules.
/// </summary>
public sealed class SponsorshipStudentTests : IDisposable
{
    private const string Base = "/api/v1/admin/sponsorship";
    private static readonly string Staff = TestTokens.Issue([Permissions.SponsorshipView, Permissions.SponsorshipStudentManage, Permissions.AuditView], subject: "usr_staff");
    private static readonly string Viewer = TestTokens.Issue([Permissions.SponsorshipView], subject: "usr_viewer");
    private static readonly string Manager = TestTokens.Issue([Permissions.SponsorshipStudentManage], subject: "usr_manager");
    private static readonly string NoKeys = TestTokens.Issue([], subject: "usr_nokeys");
    private static readonly string Donor = TestTokens.Issue([], subject: "usr_donor", extraClaims: [new Claim(KjClaims.Role, KjRoles.Donor)]);

    private readonly SponsorshipApiFactory _factory = new();
    private readonly HttpClient _client;

    public SponsorshipStudentTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---------------------------------------------------------------- create, read, edit

    [Fact]
    public async Task CreatingAStudentSavesItEncryptedAndReturnsTheEtag()
    {
        using var response = await Send(HttpMethod.Post, $"{Base}/students", Staff, Body());

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        response.Headers.ETag!.Tag.Should().Be("\"1\"");
        var body = await Json(response);
        body.GetProperty("id").GetString().Should().StartWith("stu_");
        body.GetProperty("name").GetString().Should().Be("Rahima Akter Banu");
        body.GetProperty("programme").GetProperty("name").GetString().Should().Be("Pathshala");
        body.GetProperty("links").GetArrayLength().Should().Be(0);

        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>().Students.SingleAsync();
        new[] { row.NameProtected, row.DateOfBirthProtected, row.AddressProtected, row.GuardianNameProtected, row.GuardianPhoneProtected }
            .Should().OnlyContain(v => v != null && !v.Contains("Rahima") && !v.Contains("2012") && !v.Contains("Karim") && !v.Contains("+8801"));
    }

    [Fact]
    public async Task EveryMissingRequiredFieldIsListed()
    {
        using var response = await Send(HttpMethod.Post, $"{Base}/students", Staff, new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("VALIDATION_ERROR");
        foreach (var field in new[] { "name", "preferredName", "dateOfBirth", "school", "class", "programme", "district", "guardianName" })
        {
            text.Should().Contain(field);
        }
    }

    [Fact]
    public async Task BadValuesAreRefusedFieldByField()
    {
        using var response = await Send(HttpMethod.Post, $"{Base}/students", Staff, Body(dateOfBirth: "2999-01-01", gender: "x", guardianPhone: "01711", status: "gone"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("dateOfBirth").And.Contain("gender").And.Contain("guardianPhone").And.Contain("status");
    }

    [Fact]
    public async Task AProgrammeThatIsNotAProjectIsRefusedOnThatField()
    {
        using var response = await Send(HttpMethod.Post, $"{Base}/students", Staff, Body(programme: "no-such-project"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Invalid fields: programme.");
    }

    [Fact]
    public async Task WhenContentCannotBeReachedNothingIsSaved()
    {
        _factory.Projects.Down = true;

        using var response = await Send(HttpMethod.Post, $"{Base}/students", Staff, Body());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("PROJECTS_UNAVAILABLE");
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>().Students.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheDetailHoldsEveryFieldAndTheEtag()
    {
        var id = await Create();

        using var response = await Send(HttpMethod.Get, $"{Base}/students/{id}", Viewer);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.Should().Be("\"1\"");
        var body = await Json(response);
        body.GetProperty("guardianName").GetString().Should().Be("Abdul Karim");
        body.GetProperty("guardianPhone").GetString().Should().Be("+8801711000000");
        body.GetProperty("dateOfBirth").GetString().Should().Be("2012-03-04");
        body.GetProperty("consent").GetProperty("granted").GetBoolean().Should().BeFalse();
        body.GetProperty("hasPhoto").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AnUnknownStudentIs404()
    {
        using var response = await Send(HttpMethod.Get, $"{Base}/students/stu_nope", Staff);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EditingNeedsIfMatchAndRefusesAStaleOne()
    {
        var id = await Create();

        using var missing = await Send(HttpMethod.Patch, $"{Base}/students/{id}", Staff, new { school = "New School" });
        using var stale = await Send(HttpMethod.Patch, $"{Base}/students/{id}", Staff, new { school = "New School" }, ifMatch: "\"7\"");

        missing.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task AnEditChangesOnlyWhatWasSentAndBumpsTheEtag()
    {
        var id = await Create();

        using var response = await Send(HttpMethod.Patch, $"{Base}/students/{id}", Staff, new { school = "New School", address = "" }, ifMatch: "\"1\"");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.ETag!.Tag.Should().Be("\"2\"");
        var body = await Json(response);
        body.GetProperty("school").GetString().Should().Be("New School");
        body.GetProperty("name").GetString().Should().Be("Rahima Akter Banu");
        body.GetProperty("address").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ChangingTheProgrammeChecksTheProjectAgain()
    {
        var id = await Create();

        using var response = await Send(HttpMethod.Patch, $"{Base}/students/{id}", Staff, new { programme = "ghost" }, ifMatch: "\"1\"");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheListSearchesFullNamePreferredNameAndIdAndFilters()
    {
        var first = await Create();
        await Create(name: "Sumaiya Khatun", preferredName: "Sumi", programme: "stipend");

        (await ListIds("q=Rahima")).Should().ContainSingle().Which.Should().Be(first);
        (await ListIds("q=sumi")).Should().HaveCount(1);
        (await ListIds($"q={first}")).Should().ContainSingle().Which.Should().Be(first);
        (await ListIds("programme=stipend")).Should().HaveCount(1);
        (await ListIds("sponsored=false")).Should().HaveCount(2);
        (await ListIds("sponsored=true")).Should().BeEmpty();
        (await ListIds("limit=1")).Should().HaveCount(1);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("offset=-1")]
    [InlineData("status=gone")]
    [InlineData("sponsored=maybe")]
    public async Task BadListParametersAreRefused(string query)
    {
        using var response = await Send(HttpMethod.Get, $"{Base}/students?{query}", Staff);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------- audit

    [Fact]
    public async Task ChangesAreAuditedWithoutPersonalDataAndReadsOfGuardianDetailsAreRecorded()
    {
        var id = await Create();
        (await Send(HttpMethod.Patch, $"{Base}/students/{id}", Staff, new { school = "New School", guardianName = "Someone Else" }, ifMatch: "\"1\"")).Dispose();
        (await Send(HttpMethod.Get, $"{Base}/students/{id}", Staff)).Dispose();

        using var response = await Send(HttpMethod.Get, $"{Base}/audit?entityId={id}", Staff);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        var items = (await Json(response)).GetProperty("items").EnumerateArray().ToList();
        items.Select(e => e.GetProperty("action").GetString()).Should().Contain(["create", "update", "read"]);
        text.Should().Contain("New School");
        foreach (var secret in new[] { "Rahima Akter", "Karim", "Someone Else", "2012-03-04", "+8801711000000", "Lake Road" })
        {
            text.Should().NotContain(secret);
        }
    }

    [Fact]
    public async Task TheAuditFeedNeedsTheAuditKey()
    {
        using var response = await Send(HttpMethod.Get, $"{Base}/audit", Viewer);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------- consent

    [Fact]
    public async Task ConsentCannotBeGrantedBeforeTheSignedFormIsUploaded()
    {
        var id = await Create();

        using var response = await Send(HttpMethod.Post, $"{Base}/students/{id}/consents", Staff, new { granted = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("CONSENT_FORM_REQUIRED");
    }

    [Fact]
    public async Task ConsentIsAppendedAndWithdrawingItKeepsBothRowsAndHidesPhotoAndStory()
    {
        var id = await Create();
        await UploadForm(id, "application/pdf", Pdf());
        await Grant(id);
        (await UploadPhoto(id, "image/jpeg", Jpeg())).Dispose();
        var before = await Json(await Send(HttpMethod.Get, $"{Base}/students/{id}/donor-view?lang=en", Staff));
        before.GetProperty("hasPhoto").GetBoolean().Should().BeTrue();
        before.GetProperty("story").GetString().Should().Be("Loves maths.");

        using var withdrawn = await Send(HttpMethod.Post, $"{Base}/students/{id}/consents", Staff, new { granted = false, note = "Guardian asked" });

        withdrawn.StatusCode.Should().Be(HttpStatusCode.Created);
        var after = await Json(await Send(HttpMethod.Get, $"{Base}/students/{id}/donor-view?lang=en", Staff));
        after.GetProperty("hasPhoto").GetBoolean().Should().BeFalse();
        after.TryGetProperty("story", out var story).Should().BeTrue();
        story.ValueKind.Should().Be(JsonValueKind.Null);
        var detail = await Json(await Send(HttpMethod.Get, $"{Base}/students/{id}", Staff));
        detail.GetProperty("consents").GetArrayLength().Should().Be(2);
        detail.GetProperty("consent").GetProperty("granted").GetBoolean().Should().BeFalse();
        detail.GetProperty("hasPhoto").GetBoolean().Should().BeTrue("the file stays in the staff store");
    }

    [Fact]
    public async Task RepeatingTheCurrentConsentStateIsRefused()
    {
        var id = await Create();
        using var none = await Send(HttpMethod.Post, $"{Base}/students/{id}/consents", Staff, new { granted = false });
        await UploadForm(id, "application/pdf", Pdf());
        await Grant(id);
        using var again = await Send(HttpMethod.Post, $"{Base}/students/{id}/consents", Staff, new { granted = true });

        none.StatusCode.Should().Be(HttpStatusCode.Conflict);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).Should().Contain("CONSENT_UNCHANGED");
    }

    [Fact]
    public async Task ConsentBodyMustSayGrantedOrWithdrawn()
    {
        var id = await Create();

        using var response = await Send(HttpMethod.Post, $"{Base}/students/{id}/consents", Staff, new { note = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AConsentRowCanNeverBeChangedOrRemoved()
    {
        var id = await Create();
        await UploadForm(id, "application/pdf", Pdf());
        await Grant(id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>();
        var row = await db.Consents.FirstAsync();
        row.Granted = false;
        var edit = () => db.SaveChangesAsync();
        await edit.Should().ThrowAsync<InvalidOperationException>();
        db.ChangeTracker.Clear();
        db.Consents.Remove(await db.Consents.FirstAsync());
        var remove = () => db.SaveChangesAsync();
        await remove.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---------------------------------------------------------------- the signed form

    [Fact]
    public async Task TheSignedFormIsStoredAndOpenedOnlyByStaffWithTheViewKey()
    {
        var id = await Create();
        var pdf = Pdf();
        using var put = await UploadForm(id, "application/pdf", pdf, fileName: "..\\form.pdf");

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        using var staff = await Send(HttpMethod.Get, $"{Base}/students/{id}/consent-form", Viewer);
        staff.StatusCode.Should().Be(HttpStatusCode.OK);
        staff.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await staff.Content.ReadAsByteArrayAsync()).Should().Equal(pdf);

        using var none = await Send(HttpMethod.Get, $"{Base}/students/{id}/consent-form", null);
        using var donor = await Send(HttpMethod.Get, $"{Base}/students/{id}/consent-form", Donor);
        using var otherStaff = await Send(HttpMethod.Get, $"{Base}/students/{id}/consent-form", Manager);
        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        donor.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        otherStaff.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TheFormRulesAreTypeSignatureAndSize()
    {
        var id = await Create();

        using var gif = await UploadForm(id, "image/gif", Encoding.ASCII.GetBytes("GIF89a....."));
        using var fake = await UploadForm(id, "application/pdf", Encoding.ASCII.GetBytes("not a pdf at all"));
        using var big = await UploadForm(id, "application/pdf", Pdf(5 * 1024 * 1024 + 1));
        using var notForm = await Send(HttpMethod.Put, $"{Base}/students/{id}/consent-form", Staff, new { });

        gif.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        fake.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        big.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        notForm.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task AFormThatWasNeverUploadedIs404()
    {
        var id = await Create();

        using var response = await Send(HttpMethod.Get, $"{Base}/students/{id}/consent-form", Staff);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- the photo

    [Fact]
    public async Task APhotoNeedsRecordedConsent()
    {
        var id = await Create();

        using var response = await UploadPhoto(id, "image/jpeg", Jpeg());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("CONSENT_REQUIRED");
    }

    [Fact]
    public async Task APhotoIsStoredWithItsLocationMetadataRemoved()
    {
        var id = await Consented();

        using var put = await UploadPhoto(id, "image/jpeg", Jpeg());

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        using var get = await Send(HttpMethod.Get, $"{Base}/students/{id}/photo", Viewer);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        get.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        get.Headers.CacheControl!.ToString().Should().Contain("no-store");
        var stored = await get.Content.ReadAsByteArrayAsync();
        Encoding.Latin1.GetString(stored).Should().NotContain("Exif").And.NotContain("GPS").And.NotContain("secret comment");
        stored.Take(3).Should().Equal(0xFF, 0xD8, 0xFF);
        stored.TakeLast(2).Should().Equal(0xFF, 0xD9);
    }

    [Fact]
    public async Task APngLosesItsTextChunks()
    {
        var id = await Consented();

        (await UploadPhoto(id, "image/png", Png())).Dispose();

        using var get = await Send(HttpMethod.Get, $"{Base}/students/{id}/photo", Staff);
        Encoding.Latin1.GetString(await get.Content.ReadAsByteArrayAsync()).Should().NotContain("tEXt").And.NotContain("Location=Dhaka").And.Contain("IEND");
    }

    [Fact]
    public async Task OnlyJpegOrPngUpToTwoMegabytesIsAccepted()
    {
        var id = await Consented();

        using var gif = await UploadPhoto(id, "image/gif", Encoding.ASCII.GetBytes("GIF89a....."));
        using var webp = await UploadPhoto(id, "image/webp", Encoding.ASCII.GetBytes("RIFF....WEBP"));
        using var spoof = await UploadPhoto(id, "image/jpeg", Pdf());
        using var broken = await UploadPhoto(id, "image/jpeg", [0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 0x00]);
        using var big = await UploadPhoto(id, "image/jpeg", Jpeg(2 * 1024 * 1024 + 1));

        new[] { gif, webp, spoof, broken }.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.UnsupportedMediaType);
        big.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task ThePhotoCanBeReplacedAndDeleted()
    {
        var id = await Consented();
        (await UploadPhoto(id, "image/jpeg", Jpeg())).Dispose();
        (await UploadPhoto(id, "image/png", Png())).Dispose();

        using var replaced = await Send(HttpMethod.Get, $"{Base}/students/{id}/photo", Staff);
        using var delete = await Send(HttpMethod.Delete, $"{Base}/students/{id}/photo", Staff);
        using var gone = await Send(HttpMethod.Get, $"{Base}/students/{id}/photo", Staff);
        using var again = await Send(HttpMethod.Delete, $"{Base}/students/{id}/photo", Staff);

        replaced.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Directory.GetFiles(_factory.FilesPath, "pho-*").Should().BeEmpty("the files are removed from the store");
    }

    [Fact]
    public async Task NoPhotoIsReachableWithoutAStaffViewToken()
    {
        var id = await Consented();
        (await UploadPhoto(id, "image/jpeg", Jpeg())).Dispose();

        using var none = await Send(HttpMethod.Get, $"{Base}/students/{id}/photo", null);
        using var donor = await Send(HttpMethod.Get, $"{Base}/students/{id}/photo", Donor);
        using var noKeys = await Send(HttpMethod.Get, $"{Base}/students/{id}/photo", NoKeys);

        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        donor.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        noKeys.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------- the donor view

    [Fact]
    public async Task TheDonorViewHoldsOnlyTheAllowListedFields()
    {
        var id = await Create();

        var view = await Json(await Send(HttpMethod.Get, $"{Base}/students/{id}/donor-view", Staff));

        view.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "preferredName", "class", "school", "programme", "district", "status", "hasPhoto", "story", "storyLanguage"]);
        view.GetProperty("preferredName").GetString().Should().Be("Rahima");
        view.GetProperty("story").ValueKind.Should().Be(JsonValueKind.Null, "no consent yet");
        view.ToString().Should().NotContain("Karim").And.NotContain("2012").And.NotContain("Lake Road").And.NotContain("Rahima Akter");
    }

    [Fact]
    public async Task TheStoryFollowsTheLanguageAndFallsBackToTheOther()
    {
        var id = await Consented();
        (await Send(HttpMethod.Patch, $"{Base}/students/{id}", Staff, new { storyBn = "" }, ifMatch: await EtagOf(id))).Dispose();

        var bn = await Json(await Send(HttpMethod.Get, $"{Base}/students/{id}/donor-view?lang=bn", Staff));
        var en = await Json(await Send(HttpMethod.Get, $"{Base}/students/{id}/donor-view?lang=en", Staff));

        bn.GetProperty("story").GetString().Should().Be("Loves maths.");
        bn.GetProperty("storyLanguage").GetString().Should().Be("en");
        en.GetProperty("storyLanguage").GetString().Should().Be("en");
        using var bad = await Send(HttpMethod.Get, $"{Base}/students/{id}/donor-view?lang=fr", Staff);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ProjectsForTheProgrammePickerComeFromContent()
    {
        var ok = await Json(await Send(HttpMethod.Get, $"{Base}/projects", Viewer));
        _factory.Projects.Down = true;
        using var down = await Send(HttpMethod.Get, $"{Base}/projects", Viewer);

        ok.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetString()).Should().Equal("pathshala", "stipend");
        down.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ---------------------------------------------------------------- who may call what

    public static TheoryData<string, string, string> Endpoints() => new()
    {
        { "GET", $"{Base}/projects", "view" },
        { "GET", $"{Base}/students", "view" },
        { "POST", $"{Base}/students", "manage" },
        { "GET", $"{Base}/students/stu_x", "view" },
        { "PATCH", $"{Base}/students/stu_x", "manage" },
        { "POST", $"{Base}/students/stu_x/consents", "manage" },
        { "PUT", $"{Base}/students/stu_x/photo", "manage" },
        { "GET", $"{Base}/students/stu_x/photo", "view" },
        { "DELETE", $"{Base}/students/stu_x/photo", "manage" },
        { "PUT", $"{Base}/students/stu_x/consent-form", "manage" },
        { "GET", $"{Base}/students/stu_x/consent-form", "view" },
        { "GET", $"{Base}/students/stu_x/donor-view", "view" },
        { "GET", $"{Base}/audit", "audit" }
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EveryEndpointRefusesNoTokenADonorAndATokenWithoutTheKey(string method, string path, string key)
    {
        var wrongKey = key == "view" ? Manager : Viewer;

        using var none = await Send(new HttpMethod(method), path, null, method is "POST" or "PATCH" ? new { } : null);
        using var donor = await Send(new HttpMethod(method), path, Donor, method is "POST" or "PATCH" ? new { } : null);
        using var noKeys = await Send(new HttpMethod(method), path, NoKeys, method is "POST" or "PATCH" ? new { } : null);
        using var other = await Send(new HttpMethod(method), path, wrongKey, method is "POST" or "PATCH" ? new { } : null);

        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        new[] { donor, noKeys, other }.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Forbidden);
        donor.Headers.CacheControl!.ToString().Should().Contain("no-store", "a refusal about a child record is not cacheable either");
    }

    [Fact]
    public async Task ThereIsNoWayToDeleteAStudent()
    {
        var id = await Create();

        using var response = await Send(HttpMethod.Delete, $"{Base}/students/{id}", Staff);

        response.StatusCode.Should().NotBe(HttpStatusCode.NoContent).And.NotBe(HttpStatusCode.OK);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>().Students.CountAsync()).Should().Be(1);
    }

    // ---------------------------------------------------------------- the metadata stripper

    [Fact]
    public void TheStripperKeepsPixelsAndDropsMetadata()
    {
        var jpeg = ImageSanitizer.Strip(ImageSanitizer.Jpeg, Jpeg())!;
        var png = ImageSanitizer.Strip(ImageSanitizer.Png, Png())!;

        Encoding.Latin1.GetString(jpeg).Should().NotContain("Exif").And.Contain("PIXELDATA");
        Encoding.Latin1.GetString(png).Should().NotContain("tEXt").And.Contain("IDAT");
    }

    [Fact]
    public void AFileThatIsNotAWellFormedImageIsNotPassedThrough()
    {
        ImageSanitizer.Strip(ImageSanitizer.Jpeg, [0xFF, 0xD8, 0xFF, 0xE1, 0x7F, 0xFF]).Should().BeNull();
        ImageSanitizer.Strip(ImageSanitizer.Png, Png()[..^6]).Should().BeNull();
        ImageSanitizer.Strip("image/gif", Jpeg()).Should().BeNull();
    }

    // ---------------------------------------------------------------- helpers

    private static object Body(string name = "Rahima Akter Banu", string preferredName = "Rahima", string programme = "pathshala", string dateOfBirth = "2012-03-04",
        string? gender = "female", string guardianPhone = "+8801711000000", string status = "active") => new
        {
            name,
            preferredName,
            dateOfBirth,
            gender,
            school = "Lakeview School",
            @class = "Class 6",
            programme,
            district = "Dhaka",
            address = "12 Lake Road, Dhaka",
            guardianName = "Abdul Karim",
            guardianPhone,
            storyEn = "Loves maths.",
            storyBn = "অঙ্ক ভালোবাসে।",
            status
        };

    private async Task<string> Create(string name = "Rahima Akter Banu", string preferredName = "Rahima", string programme = "pathshala")
    {
        using var response = await Send(HttpMethod.Post, $"{Base}/students", Staff, Body(name, preferredName, programme));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> Consented()
    {
        var id = await Create();
        (await UploadForm(id, "application/pdf", Pdf())).Dispose();
        await Grant(id);
        return id;
    }

    private async Task Grant(string id)
    {
        using var response = await Send(HttpMethod.Post, $"{Base}/students/{id}/consents", Staff, new { granted = true });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task<string> EtagOf(string id)
    {
        using var response = await Send(HttpMethod.Get, $"{Base}/students/{id}", Staff);
        return response.Headers.ETag!.Tag;
    }

    private async Task<List<string>> ListIds(string query)
    {
        using var response = await Send(HttpMethod.Get, $"{Base}/students?{query}", Staff);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();
    }

    private Task<HttpResponseMessage> UploadPhoto(string id, string contentType, byte[] bytes) => Upload($"{Base}/students/{id}/photo", "photo", contentType, bytes, "me.jpg");

    private Task<HttpResponseMessage> UploadForm(string id, string contentType, byte[] bytes, string fileName = "form.pdf") =>
        Upload($"{Base}/students/{id}/consent-form", "form", contentType, bytes, fileName);

    private async Task<HttpResponseMessage> Upload(string path, string field, string contentType, byte[] bytes, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, field, fileName);
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Staff);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

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

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var stream = await response.Content.ReadAsStreamAsync();
        return (await JsonDocument.ParseAsync(stream)).RootElement.Clone();
    }

    private static byte[] Pdf(int length = 0)
    {
        var head = Encoding.ASCII.GetBytes("%PDF-1.4 signed form");
        return length > head.Length ? [.. head, .. new byte[length - head.Length]] : head;
    }

    /// <summary>A small JPEG with EXIF (a GPS tag), XMP-style APP1 and a comment ahead of the scan data.</summary>
    private static byte[] Jpeg(int padTo = 0)
    {
        static byte[] Segment(byte marker, byte[] payload) => [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF), .. payload];
        var jfif = Segment(0xE0, Encoding.ASCII.GetBytes("JFIF\0\u0001\u0001\0\0\u0001\0\u0001\0\0"));
        var exif = Segment(0xE1, Encoding.ASCII.GetBytes("Exif\0\0GPSLatitude=23.8 GPSLongitude=90.4"));
        var comment = Segment(0xFE, Encoding.ASCII.GetBytes("secret comment"));
        var scanHeader = new byte[] { 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00 };
        var pixels = Encoding.ASCII.GetBytes("PIXELDATA");
        var pad = padTo > 0 ? new byte[padTo] : [];
        return [0xFF, 0xD8, .. jfif, .. exif, .. comment, .. scanHeader, .. pixels, .. pad, 0xFF, 0xD9];
    }

    /// <summary>A small PNG with a tEXt chunk that names a location.</summary>
    private static byte[] Png()
    {
        static byte[] Chunk(string type, byte[] data) =>
            [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. Encoding.ASCII.GetBytes(type), .. data, 0, 0, 0, 0];
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]),
            .. Chunk("tEXt", Encoding.ASCII.GetBytes("Comment\0Location=Dhaka")),
            .. Chunk("IDAT", [0x78, 0x9C, 0x63, 0x60, 0x00, 0x00]),
            .. Chunk("IEND", [])];
    }
}
