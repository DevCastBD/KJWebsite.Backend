using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

/// <summary>
/// The two Finance calls the Sponsorship module makes when a student is linked (BE-107, #214). They hand out almost nothing about a donor and
/// write only through the consent gate, so the tests lead with who may call and with what the gate refuses.
/// </summary>
public sealed class DonorLinkTargetTests : IDisposable
{
    private const string Donors = "/api/v1/admin/finance/donors";
    private static readonly string Linker = TestTokens.Issue([Permissions.SponsorshipLinkManage], subject: "usr_linker");
    private static readonly string Manager = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonorManage], subject: "usr_manager");

    private readonly FinanceApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-test-proofs-{Guid.NewGuid():N}");

    public DonorLinkTargetTests()
    {
        _factory.Settings["Finance:Proofs:Path"] = _proofs;
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_proofs))
        {
            Directory.Delete(_proofs, recursive: true);
        }
    }

    // ---------------------------------------------------------------- who may call

    [Fact]
    public async Task OnlyAHolderOfTheLinkPermissionMayCallEitherEndpoint()
    {
        var id = await Donor("rahim@example.org");
        var donorToken = TestTokens.Issue([], subject: "usr_rahim", extraClaims: [new Claim(KjClaims.Role, KjRoles.Donor)]);
        var wrong = new[]
        {
            null,
            Manager,
            TestTokens.Issue([Permissions.FinanceView], subject: "usr_viewer"),
            TestTokens.Issue([Permissions.SponsorshipView], subject: "usr_sponsor_viewer"),
            donorToken
        };

        foreach (var token in wrong)
        {
            var read = await Send(HttpMethod.Get, $"{Donors}/{id}/link-target", token);
            read.StatusCode.Should().Be(token is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden);
            var write = await Send(HttpMethod.Post, $"{Donors}/{id}/link-notices", token, new { linkId = "lnk_1" }, key: "k-refused");
            write.StatusCode.Should().Be(token is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden);
        }

        (await Db(db => db.Notifications.CountAsync())).Should().Be(0);
        (await Db(db => db.DonorSuppressions.CountAsync())).Should().Be(0);
    }

    // ---------------------------------------------------------------- link target

    [Fact]
    public async Task TheLinkTargetHoldsOnlyTheFactsNeededToLink()
    {
        var id = await Donor("rahim@example.org", "usr_rahim");

        using var response = await Send(HttpMethod.Get, $"{Donors}/{id}/link-target", Linker);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await Json(response);
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "accountSubject", "status", "accountStatus"]);
        body.GetProperty("id").GetString().Should().Be(id);
        body.GetProperty("accountSubject").GetString().Should().Be("usr_rahim");
        body.GetProperty("status").GetString().Should().Be(DonorStatuses.Active);
        body.GetProperty("accountStatus").GetString().Should().Be(AccountStatuses.Active);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("rahim@example.org").And.NotContain("Rahim");
    }

    [Fact]
    public async Task ADonorWithoutASignInHasNoSubjectAndAnUnknownDonorIsNotFound()
    {
        var id = await Donor("nosignin@example.org");

        var body = await Json(await Send(HttpMethod.Get, $"{Donors}/{id}/link-target", Linker));
        body.GetProperty("accountSubject").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("accountStatus").GetString().Should().Be(AccountStatuses.None);

        using var missing = await Send(HttpMethod.Get, $"{Donors}/dnr_missing/link-target", Linker);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(missing)).Should().Be("NOT_FOUND");
    }

    // ---------------------------------------------------------------- link notice and the consent gate

    [Fact]
    public async Task WithoutUpdatesConsentTheNoticeIsSuppressedAndRecordedNeverQueued()
    {
        var id = await Donor("rahim@example.org");

        using var response = await Send(HttpMethod.Post, $"{Donors}/{id}/link-notices", Linker, new { linkId = "lnk_abc" }, key: "k-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await Json(response)).GetProperty("outcome").GetString().Should().Be("suppressed");
        (await Db(db => db.Notifications.CountAsync())).Should().Be(0);
        var held = await Db(db => db.DonorSuppressions.AsNoTracking().SingleAsync());
        held.DonorId.Should().Be(id);
        held.Template.Should().Be("donor.student_linked");
        held.Purpose.Should().Be(ConsentPurposes.Updates);
    }

    [Fact]
    public async Task ConsentToThanksOrAppealsDoesNotOpenTheUpdatesGate()
    {
        var id = await Donor("rahim@example.org");
        await Grant(id, ConsentPurposes.Thanks);
        await Grant(id, ConsentPurposes.Appeals);

        (await Json(await Send(HttpMethod.Post, $"{Donors}/{id}/link-notices", Linker, new { linkId = "lnk_abc" }, key: "k-1")))
            .GetProperty("outcome").GetString().Should().Be("suppressed");

        (await Db(db => db.Notifications.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task WithUpdatesConsentOneNoticeIsQueuedInTheDonorsLanguageAndHoldsOnlyTheLinkId()
    {
        var id = await Donor("rahim@example.org", language: "bn");
        await Grant(id, ConsentPurposes.Updates);

        using var response = await Send(HttpMethod.Post, $"{Donors}/{id}/link-notices", Linker, new { linkId = "lnk_abc" }, key: "k-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await Json(response)).GetProperty("outcome").GetString().Should().Be("queued");
        var queued = await Db(db => db.Notifications.AsNoTracking().SingleAsync());
        queued.Template.Should().Be("donor.student_linked");
        queued.Language.Should().Be("bn");
        queued.SentAt.Should().BeNull();
        queued.PayloadProtected.Should().NotContain("lnk_abc", "the payload is stored protected");
        (await Db(db => db.DonorSuppressions.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task WithdrawingUpdatesConsentClosesTheGateAgain()
    {
        var id = await Donor("rahim@example.org");
        await Grant(id, ConsentPurposes.Updates);
        await Grant(id, ConsentPurposes.Updates, granted: false);

        (await Json(await Send(HttpMethod.Post, $"{Donors}/{id}/link-notices", Linker, new { linkId = "lnk_abc" }, key: "k-1")))
            .GetProperty("outcome").GetString().Should().Be("suppressed");

        (await Db(db => db.Notifications.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ARepeatedIdempotencyKeyQueuesOnlyOneNotice()
    {
        var id = await Donor("rahim@example.org");
        await Grant(id, ConsentPurposes.Updates);

        using var first = await Send(HttpMethod.Post, $"{Donors}/{id}/link-notices", Linker, new { linkId = "lnk_abc" }, key: "k-same");
        using var again = await Send(HttpMethod.Post, $"{Donors}/{id}/link-notices", Linker, new { linkId = "lnk_abc" }, key: "k-same");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        again.Headers.GetValues("Idempotency-Replayed").Should().Contain("true");
        (await Db(db => db.Notifications.CountAsync())).Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("lnk_0123456789012345678901234567890123456789")]
    public async Task AMissingOrOversizedLinkIdIsRefusedAndNothingIsWritten(string? linkId)
    {
        var id = await Donor("rahim@example.org");
        await Grant(id, ConsentPurposes.Updates);

        using var response = await Send(HttpMethod.Post, $"{Donors}/{id}/link-notices", Linker, new { linkId }, key: "k-bad");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(response)).Should().Be("VALIDATION_ERROR");
        (await Db(db => db.Notifications.CountAsync())).Should().Be(0);
        (await Db(db => db.DonorSuppressions.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ANoticeForAnUnknownDonorIsNotFoundAndNothingIsWritten()
    {
        using var response = await Send(HttpMethod.Post, $"{Donors}/dnr_missing/link-notices", Linker, new { linkId = "lnk_abc" }, key: "k-missing");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(response)).Should().Be("NOT_FOUND");
        (await Db(db => db.Notifications.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ANoticeWithoutAnIdempotencyKeyIsRefusedBecauseARepeatWouldQueueASecondEmail()
    {
        var id = await Donor("rahim@example.org");
        await Grant(id, ConsentPurposes.Updates);

        using var response = await Send(HttpMethod.Post, $"{Donors}/{id}/link-notices", Linker, new { linkId = "lnk_abc" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(response)).Should().Be("VALIDATION_ERROR");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Idempotency-Key");
        (await Db(db => db.Notifications.CountAsync())).Should().Be(0);
        (await Db(db => db.DonorSuppressions.CountAsync())).Should().Be(0);
    }

    [Fact]
    public void TheStudentLinkedTemplateMapsToTheUpdatesPurpose()
    {
        FinanceService.Donors.DonorMessenger.PurposeOf("donor.student_linked").Should().Be(ConsentPurposes.Updates);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<string> Donor(string email, string? subject = null, string language = "en")
    {
        using var response = await Send(HttpMethod.Post, Donors, Manager, new { name = "Rahim Uddin", email, language });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var id = (await Json(response)).GetProperty("donor").GetProperty("id").GetString()!;
        if (subject is not null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var donor = await db.Donors.SingleAsync(d => d.Id == id);
            donor.AccountSubject = subject;
            donor.AccountStatus = AccountStatuses.Active;
            await db.SaveChangesAsync();
        }

        return id;
    }

    private async Task Grant(string id, string purpose, bool granted = true)
    {
        using var response = await Send(HttpMethod.Post, $"{Donors}/{id}/consent", Manager, new { purpose, granted });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<T> Db<T>(Func<FinanceDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<FinanceDbContext>());
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token, object? body = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return TestControls.SendAsync(_client, request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("error").GetProperty("code").GetString();
}
