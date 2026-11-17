using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinanceService.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static KJWebsite.IntegrationTests.PrivacyFlows;

namespace KJWebsite.IntegrationTests;

public sealed class PrivacyDonorGiftNameTests(PrivacyStack stack) : IClassFixture<PrivacyStack>
{
    private const string Gifts = "/api/v1/admin/finance/donations";
    private const string History = "/api/v1/donors/me/donations";
    private const string Names = "/api/v1/donors/me/students/names?ids=";
    private const string Students = "/api/v1/admin/sponsorship/students";
    private const string Donors = "/api/v1/admin/finance/donors";

    [Fact]
    public async Task GiftNameActiveStartedLinkShowsPreferredNameThroughGateway()
    {
        var f = await Setup();
        var gift = await Gift(f);
        Named(Item(await Page(f.Token), gift), f.Name);
        await LookupName(f.Token, f.Student, f.Name);
        await NoLeaks(f);
    }

    [Fact]
    public async Task GiftNameEndedLinkKeepsFrozenNameAfterEditAndConsentWithdrawal()
    {
        var f = await Setup();
        await UploadPhoto(stack, f.Staff, f.Student);
        var gift = await Gift(f);
        await Unlink(f, "ended");
        var replacement = stack.NewSeed("replacement-preferred");
        await Patch($"{Students}/{f.Student}", f.Staff, new { preferredName = replacement });
        using var consent = await Send(stack, HttpMethod.Post, $"{Students}/{f.Student}/consents", f.Staff, new { granted = false });
        await Json(consent, HttpStatusCode.Created);
        var page = await Page(f.Token);
        Named(Item(page, gift), f.Name);
        page.ToString().Should().NotContain(replacement, "ended gift history must use the frozen name");
        var lookup = await LookupName(f.Token, f.Student, f.Name);
        lookup.ToString().Should().NotContain(replacement, "ended name lookup must use the frozen name");
        await NoLeaks(f, replacement);
    }

    [Theory]
    [InlineData("mistaken")]
    [InlineData("cancelled")]
    [InlineData("other-donor")]
    [InlineData("other-donor-link-voided")]
    public async Task GiftNameHiddenCasesHaveIdenticalPlainGiftShape(string kind)
    {
        var f = await Setup();
        var gift = await Gift(f);
        var original = Item(await Page(f.Token), gift);
        Named(original, f.Name);
        var hiddenName = await Hide(f, gift, kind);
        var hidden = Item(await Page(f.Token), gift);
        Plain(hidden);
        StableGift(hidden).Should().Be(PlainShape(), $"{kind} must have exactly the plain gift fields");
        var page = await Page(f.Token);
        page.ToString().Should().NotContain(hiddenName);
        using var lookup = await Send(stack, HttpMethod.Get, Names + (kind is "other-donor" or "other-donor-link-voided" ? await GiftStudent(gift) : f.Student), f.Token);
        (await Json(lookup, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Should().BeEmpty();
        Private(lookup);
        await NoLeaks(f, hiddenName);
    }

    [Fact]
    public async Task GiftNameBengaliDonorGetsLocalizedActiveAndHiddenLabels()
    {
        var f = await Setup();
        await Patch($"{Donors}/{f.Donor}", f.Staff, new { language = "bn" });
        var gift = await Gift(f);
        Named(Item(await Page(f.Token), gift), f.Name, "শিক্ষার্থী পৃষ্ঠপোষকতা");
        await Unlink(f, "mistaken");
        var hidden = Item(await Page(f.Token), gift);
        Plain(hidden, "শিক্ষার্থী পৃষ্ঠপোষকতা");
        StableGift(hidden).Should().Be(PlainShape("শিক্ষার্থী পৃষ্ঠপোষকতা"));
        await NoLeaks(f);
    }

    [Fact]
    public async Task GiftNameOtherDonorLookupMatchesUnknownAndHistoryNeverLeaksName()
    {
        var a = await Setup();
        var b = await Setup();
        var gift = await Gift(b);
        await Repoint(gift, a.Student);
        using var actual = await Send(stack, HttpMethod.Get, Names + a.Student, b.Token);
        using var unknown = await Send(stack, HttpMethod.Get, Names + $"stu_{Guid.NewGuid():N}", b.Token);
        (await Json(actual, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Should().BeEmpty();
        await Json(unknown, HttpStatusCode.OK);
        Private(actual);
        Private(unknown);
        (await actual.Content.ReadAsByteArrayAsync()).Should().Equal(await unknown.Content.ReadAsByteArrayAsync());
        var page = await Page(b.Token);
        Plain(Item(page, gift));
        page.ToString().Should().NotContain(a.Name, "donor B must never receive donor A's student name");
        await NoLeaks(b, a.Name);
    }

    [Fact]
    public async Task GiftNameMixedPageNamesOnlyVisibleGiftAndPreservesTotalsAndOrder()
    {
        var f = await Setup();
        var visible = await Gift(f, date: Day(-1));
        var hidden = await Gift(f);
        var general = await Gift(f, general: true);
        var before = await Page(f.Token);
        var order = before.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
        var hiddenName = await Hide(f, hidden, "other-donor");
        var after = await Page(f.Token);
        after.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()).Should().Equal(order);
        after.GetProperty("items").EnumerateArray().Select(x => (Date: x.GetProperty("date").GetString(), Receipt: x.GetProperty("receiptNumber").GetString()))
            .Should().Equal(before.GetProperty("items").EnumerateArray().Select(x => (x.GetProperty("date").GetString(), x.GetProperty("receiptNumber").GetString())));
        order.Last().Should().Be(visible);
        Named(Item(after, visible), f.Name);
        Plain(Item(after, hidden));
        var plainGeneral = Item(after, general);
        plainGeneral.GetProperty("designation").GetString().Should().Be("General");
        plainGeneral.GetProperty("studentName").ValueKind.Should().Be(JsonValueKind.Null);
        plainGeneral.GetProperty("designationId").ValueKind.Should().Be(JsonValueKind.Null);
        after.GetProperty("total").GetInt32().Should().Be(3);
        after.GetProperty("totals").ToString().Should().Be(before.GetProperty("totals").ToString());
        var total = after.GetProperty("totals").EnumerateArray().Should().ContainSingle().Subject;
        total.GetProperty("currency").GetString().Should().Be("BDT");
        total.GetProperty("cash").GetString().Should().Be("1500.00");
        total.GetProperty("inKind").GetString().Should().Be("0.00");
        after.ToString().Should().NotContain(hiddenName);
        await NoLeaks(f, hiddenName);
    }

    private sealed record Fixture(string Staff, string Checker, string Donor, string Token, string Student, string Name);

    private async Task<Fixture> Setup()
    {
        var staff = await stack.NewSuperAdminAsync();
        var checker = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        await Patch($"{Donors}/{donor.Id}", staff.Token, new { language = "en" });
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        using var response = await Send(stack, HttpMethod.Get, $"{Students}/{student}", staff.Token);
        var name = (await Json(response, HttpStatusCode.OK)).GetProperty("preferredName").GetString()!;
        return new(staff.Token, checker.Token, donor.Id, await ChoosePassword(donor.Email, donor.Password), student, name);
    }

    private async Task<string> ChoosePassword(string email, string initial)
    {
        var token = await stack.LoginAsync(email, initial);
        using var response = await Send(stack, HttpMethod.Post, "/api/v1/auth/change-password", token,
            new { currentPassword = initial, newPassword = stack.NewSeed("ChosenPassword9") });
        var session = await Json(response, HttpStatusCode.OK);
        session.GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();
        return session.GetProperty("access_token").GetString()!;
    }

    private async Task<string> Gift(Fixture f, bool general = false, string? date = null)
    {
        using var response = await Send(stack, HttpMethod.Post, Gifts, f.Staff,
            new { donorId = f.Donor, amount = "500.00", currency = "BDT", method = "cash", date, designation = general ? "general" : "student", designationId = general ? null : f.Student });
        var id = (await Json(response, HttpStatusCode.Created)).GetProperty("id").GetString()!;
        using var approved = await Send(stack, HttpMethod.Post, $"{Gifts}/{id}/approve", f.Checker);
        await Json(approved, HttpStatusCode.OK);
        return id;
    }

    private async Task<string> Hide(Fixture f, string gift, string kind)
    {
        if (kind is "mistaken" or "cancelled")
        {
            await Unlink(f, "mistaken");
            if (kind == "cancelled")
            {
                using var future = await Send(stack, HttpMethod.Post, $"/api/v1/admin/sponsorship/donors/{f.Donor}/students", f.Staff,
                    new { studentId = f.Student, startDate = Day(2) });
                await Json(future, HttpStatusCode.Created);
                var ended = await Unlink(f, "ended");
                ended.GetProperty("endDate").ValueKind.Should().Be(JsonValueKind.Null);
            }
            return f.Name;
        }

        var other = await CreateDonor(stack, f.Staff);
        var student = await LinkStudent(stack, f.Staff, other.Id);
        using var response = await Send(stack, HttpMethod.Get, $"{Students}/{student}", f.Staff);
        var name = (await Json(response, HttpStatusCode.OK)).GetProperty("preferredName").GetString()!;
        if (kind == "other-donor-link-voided")
        {
            using var unlinked = await Send(stack, HttpMethod.Post, $"/api/v1/admin/sponsorship/donors/{other.Id}/students/{student}/unlink", f.Staff,
                new { type = "mistaken", reason = stack.NewSeed("unlink-reason") });
            await Json(unlinked, HttpStatusCode.OK);
        }
        await Repoint(gift, student);
        return name;
    }

    private async Task Repoint(string gift, string student)
    {
        using var scope = stack.Finance.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        (await db.Donations.Where(x => x.Id == gift).ExecuteUpdateAsync(s => s.SetProperty(x => x.DesignationId, student))).Should().Be(1);
        (await db.Receipts.Where(x => x.DonationId == gift).ExecuteUpdateAsync(s => s.SetProperty(x => x.DesignationId, student))).Should().Be(1);
    }

    private async Task<string> GiftStudent(string gift)
    {
        using var scope = stack.Finance.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Donations.SingleAsync(x => x.Id == gift)).DesignationId!;
    }

    private async Task<JsonElement> Unlink(Fixture f, string type)
    {
        using var response = await Send(stack, HttpMethod.Post, $"/api/v1/admin/sponsorship/donors/{f.Donor}/students/{f.Student}/unlink", f.Staff,
            new { type, endDate = Day(), reason = stack.NewSeed("unlink-reason") });
        return await Json(response, HttpStatusCode.OK);
    }

    private async Task Patch(string path, string token, object body)
    {
        using var current = await Send(stack, HttpMethod.Get, path, token);
        await Json(current, HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        using var response = await stack.Gateway.SendAsync(request);
        await Json(response, HttpStatusCode.OK);
    }

    private async Task<JsonElement> Page(string token)
    {
        using var response = await Send(stack, HttpMethod.Get, History, token);
        var page = await Json(response, HttpStatusCode.OK);
        Private(response);
        return page;
    }

    private async Task<JsonElement> LookupName(string token, string student, string name)
    {
        using var response = await Send(stack, HttpMethod.Get, Names + student, token);
        var body = await Json(response, HttpStatusCode.OK);
        Private(response);
        var item = body.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("id").GetString().Should().Be(student);
        item.GetProperty("preferredName").GetString().Should().Be(name);
        return body;
    }

    private async Task NoLeaks(Fixture f, params string[] additionalNames)
    {
        foreach (var (path, token, status) in new[]
        {
            ("/api/v1/donors/me", f.Token, HttpStatusCode.OK),
            ($"{Donors}/{f.Donor}", f.Staff, HttpStatusCode.OK),
            ("/api/v1/content/settings", (string?)null, HttpStatusCode.OK),
            (Gifts, f.Token, HttpStatusCode.Forbidden)
        })
        {
            using var response = await Send(stack, HttpMethod.Get, path, token);
            await Json(response, status);
            var body = await response.Content.ReadAsStringAsync();
            foreach (var name in additionalNames.Prepend(f.Name))
                body.Should().NotContain(name, $"GET {path} must not expose a student preferred name");
        }
        GlobalLogSink.Default.AssertNone();
        GlobalLogSink.Default.Violations.Should().BeEmpty();
    }

    private static JsonElement Item(JsonElement page, string id) => page.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);

    private static void Named(JsonElement gift, string name, string label = "Student sponsorship")
    {
        gift.GetProperty("studentName").GetString().Should().Be(name, "visible gifts must show the permitted preferred name");
        gift.GetProperty("designation").GetString().Should().Be($"{label}: {name}");
        gift.GetProperty("designationId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static void Plain(JsonElement gift, string label = "Student sponsorship")
    {
        gift.GetProperty("studentName").ValueKind.Should().Be(JsonValueKind.Null, "hidden student gifts must never disclose a name");
        gift.GetProperty("designation").GetString().Should().Be(label);
        gift.GetProperty("designationId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static string StableGift(JsonElement gift)
    {
        var node = JsonNode.Parse(gift.GetRawText())!.AsObject();
        foreach (var key in new[] { "id", "reference", "receiptNumber", "date" }) node.Remove(key);
        return node.ToJsonString();
    }

    private static string PlainShape(string label = "Student sponsorship") => JsonSerializer.Serialize(new
    {
        amount = "500.00",
        currency = "BDT",
        designation = label,
        designationType = "student",
        designationId = (string?)null,
        studentName = (string?)null,
        status = "confirmed",
        inKind = false
    });

    private static string Day(int offset = 0) => DateTime.UtcNow.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
