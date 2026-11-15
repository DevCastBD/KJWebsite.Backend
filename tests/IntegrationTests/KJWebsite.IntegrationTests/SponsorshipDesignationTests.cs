using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SponsorshipService.Data;

namespace KJWebsite.IntegrationTests;

public sealed class SponsorshipDesignationTests : IDisposable
{
    private const string Base = "/api/v1/admin/sponsorship";
    private const string DonorId = "dnr_x";
    private static readonly string Finance = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonationRecord], subject: "usr_fin");
    private static readonly string Record = TestTokens.Issue([Permissions.FinanceDonationRecord]);
    private static readonly string View = TestTokens.Issue([Permissions.FinanceView]);
    private static readonly string Staff = TestTokens.Issue([Permissions.SponsorshipView, Permissions.SponsorshipStudentManage, Permissions.SponsorshipLinkManage]);
    private readonly SponsorshipLinkApiFactory _factory = new() { TimeZoneId = "Asia/Dhaka" };
    private readonly HttpClient _client;

    public SponsorshipDesignationTests()
    {
        _factory.Finance.Donors[DonorId] = new(DonorId, "usr_donor");
        _client = _factory.CreateClient();
    }

    public void Dispose() { _client.Dispose(); _factory.Dispose(); }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RefusesMissingDonorSponsorshipAndWrongFinancePermissions(int endpoint)
    {
        var id = Id();
        var path = endpoint switch
        {
            0 => Designation(id) + "?date=" + Date(),
            1 => Designatable() + "?date=" + Date(),
            _ => Names(id)
        };
        var donor = TestTokens.Issue([], extraClaims: [new Claim(KjClaims.Role, KjRoles.Donor)]);
        var sponsorship = TestTokens.Issue([Permissions.SponsorshipView, Permissions.SponsorshipLinkManage]);
        var before = await AuditCount();
        foreach (var (token, status) in new[] { ((string?)null, 401), (donor, 403), (sponsorship, 403),
            (TestTokens.Issue([Permissions.SponsorshipView]), 403), (endpoint == 2 ? Record : View, 403) })
            await Get(path, token, status);
        await Get(path, endpoint == 2 ? View : Record);
        await Get(path, Finance);
        (await AuditCount()).Should().Be(before);
    }

    [Fact]
    public async Task EmptyBoundQueryParametersNameTheirInvalidFields()
    {
        await Invalid(Designation(Id()) + "?date=", "date");
        await Invalid(Designatable() + "?date=", "date");
        await Invalid(Names(""), "ids");
    }

    [Fact]
    public async Task DesignationChecksEveryHistoricalWindowAndReturnsOnlyActivePreferredName()
    {
        var id = await Create();
        await SeedLinks(new LinkEntity { StudentId = id, DonorId = DonorId, StartDate = Day(-30), EndType = "ended", EndDate = Day(-20) },
            new LinkEntity { StudentId = id, DonorId = DonorId, StartDate = Day(-10) });
        var before = await AuditCount();
        foreach (var (offset, active) in new[] { (-31, false), (-30, true), (-25, true), (-20, true), (-19, false), (-11, false), (-10, true), (0, true) })
            await CheckDesignation(id, offset, active);
        await CheckDesignation(id, 0, false, "dnr_other");
        await CheckDesignation(Id(), 0, false);
        await CheckDesignation(id, 0, false, "dnr_unknown");
        (await AuditCount()).Should().Be(before);
    }

    [Theory]
    [InlineData("ended")]
    [InlineData("mistaken")]
    public async Task CancelledAndMistakenNeverCount(string type)
    {
        var id = await SeedStudent("Rahima");
        await SeedLinks(new LinkEntity { StudentId = id, DonorId = DonorId, StartDate = Day(-10), EndType = type });
        var before = await AuditCount();
        await CheckDesignation(id, -11, false);
        await CheckDesignation(id, -10, false);
        await CheckDesignation(id, 10, false);
        (await Items(Designatable() + "?date=" + Date(10))).Should().BeEmpty();
        (await AuditCount()).Should().Be(before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-1-02")]
    [InlineData("2026-01-2")]
    [InlineData("2026-02-30")]
    [InlineData("2026-01-02T00:00:00Z")]
    [InlineData("garbage")]
    public async Task DatesAreStrictAndDesignationRequiresDate(string? date)
    {
        var before = await AuditCount();
        await Invalid(Designation(Id()) + (date is null ? "" : "?date=" + date), "date");
        if (date is not null) await Invalid(Designatable() + "?date=" + date, "date");
        (await AuditCount()).Should().Be(before);
    }

    [Fact]
    public async Task DesignatableUsesFoundationTodayAndHistoricalLinksWithStableOrderingAndLatestStart()
    {
        var z = await SeedStudent("Zulu", "inactive");
        var a = await SeedStudent("alpha", "graduated");
        var b = await SeedStudent("ALPHA");
        await SeedLinks(new LinkEntity { StudentId = z, DonorId = DonorId, StartDate = Day(-50), EndType = "ended", EndDate = Day(1) },
            new LinkEntity { StudentId = z, DonorId = DonorId, StartDate = Day(-20) },
            new LinkEntity { StudentId = a, DonorId = DonorId, StartDate = Day(-10) },
            new LinkEntity { StudentId = b, DonorId = DonorId, StartDate = Day(-10) });
        foreach (var (type, start, end, donor) in new (string?, int, int?, string)[]
        {
            (null, -10, null, "dnr_other"), ("mistaken", -10, null, DonorId),
            ("ended", -10, null, DonorId), ("ended", -10, -1, DonorId), (null, 1, null, DonorId)
        })
        {
            var excluded = await SeedStudent("Excluded");
            await SeedLinks(new LinkEntity { StudentId = excluded, DonorId = donor, StartDate = Day(start), EndType = type, EndDate = end is null ? null : Day(end.Value) });
        }
        var before = await AuditCount();
        var items = await Items(Designatable());
        items.Select(x => x.GetProperty("studentId").GetString()).Should().Equal(new[] { a, b }.Order(StringComparer.Ordinal).Append(z));
        foreach (var item in items) Properties(item, "studentId", "preferredName", "startDate", "endDate");
        items[^1].GetProperty("startDate").GetString().Should().Be(Date(-20));
        items[^1].GetProperty("endDate").ValueKind.Should().Be(JsonValueKind.Null);
        var explicitItems = await Items(Designatable() + "?date=" + Date());
        explicitItems.Select(x => x.GetRawText()).Should().Equal(items.Select(x => x.GetRawText()));
        var historic = await Items(Designatable() + "?date=" + Date(-30));
        historic.Should().ContainSingle();
        historic[0].GetProperty("startDate").GetString().Should().Be(Date(-50));
        historic[0].GetProperty("endDate").GetString().Should().Be(Date(1));
        (await AuditCount()).Should().Be(before);
    }

    [Fact]
    public async Task DesignatableCapsAtTwoHundredAfterOrdering()
    {
        for (var i = 0; i < 201; i++)
        {
            var id = await SeedStudent(i.ToString("D3", CultureInfo.InvariantCulture));
            await SeedLinks(new LinkEntity { StudentId = id, DonorId = DonorId, StartDate = Day(-1) });
        }
        var before = await AuditCount();
        var items = await Items(Designatable());
        items.Should().HaveCount(200);
        items[0].GetProperty("preferredName").GetString().Should().Be("000");
        items[^1].GetProperty("preferredName").GetString().Should().Be("199");
        (await AuditCount()).Should().Be(before);
    }

    [Fact]
    public async Task NamesBatchTrimsDeduplicatesAndOmitsUnknownWithoutDecrypting()
    {
        var a = await SeedStudent("A");
        var b = await SeedStudent("B");
        var before = await AuditCount();
        var items = await Items(Names($" {b} ,{Id()},{a},{b}, {a} "));
        items.Select(x => x.GetProperty("id").GetString()).Should().Equal(b, a);
        items.Select(x => x.GetProperty("preferredName").GetString()).Should().Equal("B", "A");
        foreach (var item in items) Properties(item, "id", "preferredName");
        var hundred = Enumerable.Range(0, 99).Select(_ => Id()).Prepend(a).ToArray();
        (await Items(Names(string.Join(',', hundred)))).Should().ContainSingle();
        (await Items(Names(string.Join(',', hundred.Append(a))))).Should().ContainSingle();
        await Invalid(Names(string.Join(',', hundred.Append(Id()))), "ids");
        (await AuditCount()).Should().Be(before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(",")]
    [InlineData("stu_bad")]
    [InlineData("stu_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("stu_00000000000000000000000000000000,")]
    public async Task NamesRefusesEmptyAndMalformedIds(string? ids)
    {
        var before = await AuditCount();
        await Invalid(ids is null ? Base + "/students/names" : Names(ids), "ids");
        (await AuditCount()).Should().Be(before);
    }

    [Fact]
    public async Task LiteralNamesRouteLeavesStudentDetailRouteAvailableToStaff()
    {
        var id = await Create();
        var detail = await Get($"{Base}/students/{id}", Staff);
        detail.GetProperty("id").GetString().Should().Be(id);
        var before = await AuditCount();
        (await Items(Names(id))).Should().ContainSingle();
        (await AuditCount()).Should().Be(before);
    }

    private async Task CheckDesignation(string id, int offset, bool active, string donor = DonorId)
    {
        var json = await Get(Designation(id, donor) + "?date=" + Date(offset));
        Properties(json, "donorId", "studentId", "date", "active", "studentName");
        json.GetProperty("donorId").GetString().Should().Be(donor);
        json.GetProperty("studentId").GetString().Should().Be(id);
        json.GetProperty("date").GetString().Should().Be(Date(offset));
        json.GetProperty("active").GetBoolean().Should().Be(active);
        json.GetProperty("studentName").GetString().Should().Be(active ? "Rahima" : null);
    }

    private async Task<JsonElement> Get(string path, string? token, int status = 200)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        ((int)response.StatusCode).Should().Be(status, await response.Content.ReadAsStringAsync());
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeTrue();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private Task<JsonElement> Get(string path) => Get(path, Finance);
    private async Task<List<JsonElement>> Items(string path)
    {
        var json = await Get(path);
        Properties(json, "items");
        return json.GetProperty("items").EnumerateArray().ToList();
    }

    private async Task Invalid(string path, string field)
    {
        var json = await Get(path, Finance, 400);
        json.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
        Properties(json.GetProperty("errors"), field);
    }

    private static void Properties(JsonElement json, params string[] names) =>
        json.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(names);
    private static string Id() => $"stu_{Guid.NewGuid():N}";
    private DateOnly Day(int offset = 0) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(_factory.TimeZoneId)).DateTime).AddDays(offset);
    private string Date(int offset = 0) => Day(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Designation(string id, string donor = DonorId) => $"{Base}/donors/{donor}/students/{id}/designation";
    private static string Designatable() => $"{Base}/donors/{DonorId}/designatable-students";
    private static string Names(string ids) => $"{Base}/students/names?ids={Uri.EscapeDataString(ids)}";

    private async Task<string> Create()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/students");
        request.Headers.Authorization = new("Bearer", Staff);
        request.Content = JsonContent.Create(new
        {
            name = "Rahima Akter Banu",
            preferredName = "Rahima",
            dateOfBirth = "2012-03-04",
            school = "Lakeview School",
            @class = "Class 6",
            programme = "pathshala",
            district = "Dhaka",
            guardianName = "Abdul Karim",
            status = "active"
        });
        using var response = await _client.SendAsync(request);
        ((int)response.StatusCode).Should().Be(201, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> SeedStudent(string name, string status = "active")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>();
        var id = Id();
        // Invalid ciphertext ensures these lookups never decrypt private student data.
        db.Students.Add(new StudentEntity { Id = id, PreferredName = name, Status = status, NameProtected = "private", DateOfBirthProtected = "private", GuardianNameProtected = "private" });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedLinks(params LinkEntity[] links)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>();
        db.Links.AddRange(links);
        await db.SaveChangesAsync();
    }

    private async Task<int> AuditCount()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>().Set<AuditEvent>().CountAsync();
    }
}
