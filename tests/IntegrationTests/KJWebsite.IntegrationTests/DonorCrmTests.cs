using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthIdentityService.Data.Entities;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

/// <summary>
/// Donor profiles and donor sign-in accounts (BE-104, #211): Finance against a real Identity in the same process. High blast radius
/// (personal data, calls to Identity, how a gift finds its donor, concurrent edits), so failures carry most of the tests.
/// </summary>
public sealed class DonorCrmTests : IDisposable
{
    private const string Donors = "/api/v1/admin/finance/donors";
    private const string Initial = "Staff-Chosen-Pass-1";
    private const string Own = "My-Own-Strong-Pass-2";
    private static readonly string Viewer = TestTokens.Issue([Permissions.FinanceView], subject: "usr_viewer");
    private static readonly string Manager = TestTokens.Issue([Permissions.FinanceView, Permissions.FinanceDonorManage], subject: "usr_manager");

    private readonly AuthApiFactory _auth = new();
    private readonly FinanceApiFactory _finance = new();
    private readonly HttpClient _authClient;
    private readonly HttpClient _client;
    private readonly string _proofs = Path.Combine(Path.GetTempPath(), $"kj-test-proofs-{Guid.NewGuid():N}");

    public DonorCrmTests()
    {
        _authClient = _auth.CreateClient();
        _finance.AuthHandler = _auth.Server.CreateHandler();
        _finance.Settings["Finance:Proofs:Path"] = _proofs;
        _client = _finance.CreateClient();
    }

    public void Dispose()
    {
        _authClient.Dispose();
        _client.Dispose();
        _auth.Dispose();
        _finance.Dispose();
        if (Directory.Exists(_proofs))
        {
            Directory.Delete(_proofs, recursive: true);
        }
    }

    // ---------------------------------------------------------------- profiles

    [Fact]
    public async Task CreatingAProfileReturnsItWithAnETagListsItBeforeAnyGiftAndStoresPersonalFieldsEncrypted()
    {
        using var response = await Send(HttpMethod.Post, Donors, Manager, new
        {
            name = "Rahim Uddin",
            email = "Rahim@Example.org",
            phone = "+8801711000001",
            language = "bn",
            type = "individual",
            address = "12 Lake Road, Dhaka",
            country = "BD",
            notes = "Prefers a call on Fridays."
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.ETag!.Tag.Should().Be("\"1\"");
        var body = await Json(response);
        var id = body.GetProperty("donor").GetProperty("id").GetString()!;
        id.Should().StartWith("dnr_");
        body.GetProperty("donor").GetProperty("email").GetString().Should().Be("Rahim@Example.org");
        body.GetProperty("donor").GetProperty("total").GetString().Should().Be("0.00");
        body.GetProperty("profile").GetProperty("address").GetString().Should().Be("12 Lake Road, Dhaka");
        body.GetProperty("profile").GetProperty("notes").GetString().Should().Be("Prefers a call on Fridays.");
        body.GetProperty("profile").GetProperty("accountStatus").GetString().Should().Be("none");

        // Listed although no gift has arrived, and stored encrypted.
        (await List()).EnumerateArray().Select(d => d.GetProperty("id").GetString()).Should().Contain(id);
        var row = await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id));
        row.AddressProtected.Should().NotBeNullOrEmpty().And.NotContain("Lake Road");
        row.NotesProtected.Should().NotBeNullOrEmpty().And.NotContain("Fridays");
        row.NameProtected.Should().NotContain("Rahim");
        row.EmailKey.Should().NotBeNullOrEmpty().And.NotContain("example");
    }

    [Fact]
    public async Task ARepeatedEmailInAnyCaseIsRefusedAndNamesTheExistingDonor()
    {
        var first = await Create("dup@example.org");

        using var again = await Send(HttpMethod.Post, Donors, Manager, new { name = "Other", email = "DUP@example.org" });

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(again)).Should().Be("DONOR_EXISTS");
        (await again.Content.ReadAsStringAsync()).Should().Contain(first);
        (await Db(db => db.Donors.CountAsync(d => d.EmailKey != ""))).Should().Be(1);
    }

    [Theory]
    [InlineData("", "a@example.org", null, null, null, null, "name")]
    [InlineData("A", "not-an-email", null, null, null, null, "email")]
    [InlineData("A", "b@example.org", "01711000001", null, null, null, "phone")]
    [InlineData("A", "c@example.org", null, "fr", null, null, "language")]
    [InlineData("A", "d@example.org", null, null, "robot", null, "type")]
    [InlineData("A", "e@example.org", null, null, "organisation", null, "organisation")]
    [InlineData("A", "f@example.org", null, null, null, "bd", "country")]
    public async Task InvalidInputIsRefusedListingTheFieldAndNothingIsCreated(string name, string email, string? phone, string? language, string? type, string? country, string field)
    {
        using var response = await Send(HttpMethod.Post, Donors, Manager, new { name, email, phone, language, type, country });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(response)).Should().Be("VALIDATION_ERROR");
        (await response.Content.ReadAsStringAsync()).Should().Contain(field);
        (await Db(db => db.Donors.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task OverlongFieldsAreRefused()
    {
        foreach (var body in new object[]
        {
            new { name = new string('a', 201), email = "long1@example.org" },
            new { name = "A", email = $"{new string('a', 250)}@example.org" },
            new { name = "A", email = "long3@example.org", address = new string('a', 501) },
            new { name = "A", email = "long4@example.org", notes = new string('a', 2001) }
        })
        {
            using var response = await Send(HttpMethod.Post, Donors, Manager, body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await Db(db => db.Donors.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task AGiftFromAProfilesEmailJoinsThatDonorInsteadOfMakingASecond()
    {
        var id = await Create("joins@example.org");

        await Gift("JOINS@example.org", "750.00");

        (await List()).EnumerateArray().Should().ContainSingle();
        var detail = await Get(id);
        detail.GetProperty("gifts").GetArrayLength().Should().Be(1);
        detail.GetProperty("donor").GetProperty("total").GetString().Should().Be("750.00");
    }

    [Fact]
    public async Task UpdatingNeedsTheETagAndRefusesAStaleOneAndANewETagFollowsEveryChange()
    {
        var id = await Create("etag@example.org");

        using var without = await Send(HttpMethod.Patch, $"{Donors}/{id}", Manager, new { name = "New Name" });
        without.StatusCode.Should().Be((HttpStatusCode)428);
        (await ErrorCode(without)).Should().Be("PRECONDITION_REQUIRED");

        using var stale = await Send(HttpMethod.Patch, $"{Donors}/{id}", Manager, new { name = "New Name" }, ifMatch: "\"7\"");
        stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        (await ErrorCode(stale)).Should().Be("PRECONDITION_FAILED");

        using var ok = await Send(HttpMethod.Patch, $"{Donors}/{id}", Manager, new { name = "New Name" }, ifMatch: "\"1\"");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Headers.ETag!.Tag.Should().Be("\"2\"");

        using var again = await Send(HttpMethod.Patch, $"{Donors}/{id}", Manager, new { phone = "+8801711000002" }, ifMatch: "W/\"1\"");
        again.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed, "two staff cannot both win on the same version");
        (await Get(id)).GetProperty("donor").GetProperty("name").GetString().Should().Be("New Name");
        _ = (await Send(HttpMethod.Get, $"{Donors}/{id}", Viewer)).Headers.ETag!.Tag.Should().Be("\"2\"");
    }

    [Fact]
    public async Task AnUpdateChangesOnlyWhatWasSentAndAnEmptyStringClearsAnOptionalField()
    {
        var id = await Create("partial@example.org", phone: "+8801711000003", address: "Old Street", notes: "A note");

        (await Patch(id, "\"1\"", new { address = "New Street" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await Get(id);
        detail.GetProperty("profile").GetProperty("address").GetString().Should().Be("New Street");
        detail.GetProperty("profile").GetProperty("notes").GetString().Should().Be("A note");
        detail.GetProperty("phone").GetString().Should().Be("+8801711000003");

        (await Patch(id, "\"2\"", new { notes = "", phone = "" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = await Get(id);
        cleared.GetProperty("profile").GetProperty("notes").ValueKind.Should().Be(JsonValueKind.Null);
        cleared.GetProperty("phone").ValueKind.Should().Be(JsonValueKind.Null);
        (await Patch(id, "\"3\"", new { name = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Patch(id, "\"3\"", new { status = "banana" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Patch("dnr_missing", "\"1\"", new { name = "X" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- the email can change and history stays

    [Fact]
    public async Task ChangingTheEmailKeepsHistoryAndNewGiftsJoinFromTheNewAddressWhileTheOldAddressStartsAnotherDonor()
    {
        await Gift("old@example.org", "1000.00");
        var id = (await List()).EnumerateArray().Single().GetProperty("id").GetString()!;
        var before = await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id));
        var receiptKeysBefore = await Db(db => db.Receipts.AsNoTracking().Select(r => r.DonorKey).ToListAsync());

        (await Patch(id, "\"1\"", new { email = "new@example.org" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id));
        after.DonorKey.Should().Be(before.DonorKey, "the donor's own key never changes");
        after.EmailKey.Should().NotBe(before.EmailKey);
        (await Db(db => db.Receipts.AsNoTracking().Select(r => r.DonorKey).ToListAsync())).Should().Equal(receiptKeysBefore, "no stored receipt is edited");
        (await Get(id)).GetProperty("gifts").GetArrayLength().Should().Be(1);

        // A gift from the new address joins the same donor.
        await Gift("new@example.org", "500.00");
        (await Get(id)).GetProperty("donor").GetProperty("total").GetString().Should().Be("1500.00");
        (await List()).EnumerateArray().Should().ContainSingle();

        // A gift from the old address starts a different donor, on a key that does not collide with the first one's.
        await Gift("old@example.org", "200.00");
        var donors = await Db(db => db.Donors.AsNoTracking().ToListAsync());
        donors.Should().HaveCount(2);
        donors.Select(d => d.DonorKey).Should().OnlyHaveUniqueItems();
        (await Get(id)).GetProperty("donor").GetProperty("total").GetString().Should().Be("1500.00", "the old-address gift is not this donor's");
    }

    [Fact]
    public async Task AnEmailAnotherDonorHoldsCannotBeTakenAndADonorWithASignInCannotChangeIt()
    {
        var first = await Create("first@example.org");
        var second = await Create("second@example.org");

        using var taken = await Patch(second, "\"1\"", new { email = "FIRST@example.org" });
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(taken)).Should().Be("DONOR_EXISTS");
        (await Patch(first, "\"1\"", new { email = "first@example.org" })).StatusCode.Should().Be(HttpStatusCode.OK, "the same email is not a change");

        var admin = await Staff("admin-email@example.org");
        (await Send(HttpMethod.Post, $"{Donors}/{second}/login", admin, new { password = Initial })).StatusCode.Should().Be(HttpStatusCode.Created);
        using var locked = await Patch(second, "\"2\"", new { email = "elsewhere@example.org" });
        locked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(locked)).Should().Be("EMAIL_HAS_ACCOUNT");
    }

    // ---------------------------------------------------------------- permissions and filters

    [Fact]
    public async Task OnlyTheRightPermissionMayCreateEditOrGiveASignIn()
    {
        var id = await Create("perm@example.org");
        var body = new { name = "X", email = "perm2@example.org" };

        (await Send(HttpMethod.Post, Donors, null, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, Donors, Viewer, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Patch, $"{Donors}/{id}", Viewer, new { name = "Y" }, ifMatch: "\"1\"")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        foreach (var (method, url) in new[] { (HttpMethod.Post, $"{Donors}/{id}/login"), (HttpMethod.Put, $"{Donors}/{id}/login/password"), (HttpMethod.Put, $"{Donors}/{id}/login/status") })
        {
            using var refused = await Send(method, url, Manager, new { password = Initial, status = "disabled" });
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
        }

        (await Db(db => db.Donors.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task TheListFiltersByStatusAccountStatusAndHasAnAccountAndRefusesUnknownValues()
    {
        var active = await Create("active@example.org");
        var inactive = await Create("inactive@example.org");
        var withAccount = await Create("account@example.org");
        (await Patch(inactive, "\"1\"", new { status = "inactive" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var admin = await Staff("admin-filter@example.org");
        (await Send(HttpMethod.Post, $"{Donors}/{withAccount}/login", admin, new { password = Initial })).StatusCode.Should().Be(HttpStatusCode.Created);

        async Task<string[]> Ids(string query) => (await List(query)).EnumerateArray().Select(d => d.GetProperty("id").GetString()!).ToArray();

        (await Ids("?status=inactive")).Should().Equal(inactive);
        (await Ids("?status=active")).Should().BeEquivalentTo([active, withAccount]);
        (await Ids("?hasAccount=true")).Should().Equal(withAccount);
        (await Ids("?hasAccount=false")).Should().BeEquivalentTo([active, inactive]);
        (await Ids("?accountStatus=active")).Should().Equal(withAccount);
        (await Ids("?accountStatus=none")).Should().BeEquivalentTo([active, inactive]);
        using var bad = await Send(HttpMethod.Get, $"{Donors}?status=banana", Viewer);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var badAccount = await Send(HttpMethod.Get, $"{Donors}?accountStatus=gone", Viewer);
        badAccount.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------- sign-in accounts through Identity

    [Fact]
    public async Task GivingASignInCreatesADonorAccountInIdentityStoresTheSubjectAndNeverReturnsThePassword()
    {
        var id = await Create("login@example.org");
        var admin = await Staff("admin-login@example.org");

        using var response = await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(Initial);
        var view = JsonDocument.Parse(raw).RootElement;
        view.GetProperty("accountStatus").GetString().Should().Be("active");
        view.GetProperty("accountShared").GetBoolean().Should().BeFalse();
        view.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();

        var donor = await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id));
        donor.AccountSubject.Should().StartWith("usr_");
        donor.Version.Should().Be(2);
        using (var scope = _auth.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
            var user = (await users.FindByIdAsync(donor.AccountSubject!))!;
            user.Email.Should().Be("login@example.org");
            user.MustChangePassword.Should().BeTrue();
            (await users.GetRolesAsync(user)).Should().Equal(KjRoles.Donor);
        }

        (await Get(id, admin)).GetProperty("profile").GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        using var donorLogin = await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "login@example.org", password = Initial });
        donorLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(donorLogin)).GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ASecondSignInIsRefusedAndAWeakPasswordCreatesNothing()
    {
        var id = await Create("twice@example.org");
        var other = await Create("weak@example.org");
        var admin = await Staff("admin-twice@example.org");

        (await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial })).StatusCode.Should().Be(HttpStatusCode.Created);
        using var again = await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(again)).Should().Be("ACCOUNT_EXISTS");

        using var weak = await Send(HttpMethod.Post, $"{Donors}/{other}/login", admin, new { password = "short" });
        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(weak)).Should().Be("VALIDATION_ERROR");
        (await weak.Content.ReadAsStringAsync()).Should().NotContain("short\"");
        (await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == other))).AccountSubject.Should().BeNull();
        using var scope = _auth.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>().FindByEmailAsync("weak@example.org")).Should().BeNull();
        (await Send(HttpMethod.Post, $"{Donors}/{other}/login", admin, new { password = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Donors}/dnr_missing/login", admin, new { password = Initial })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ADonorWhoIsAlreadyAMemberSharesTheAccountAndTheDonorEndpointsLeaveItAlone()
    {
        await CreateAuthUser("member@example.org", KjRoles.Member, "Member-Own-Pass-77");
        var id = await Create("member@example.org");
        var admin = await Staff("admin-member@example.org");

        using var response = await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial });

        response.StatusCode.Should().Be(HttpStatusCode.Created.Equals(response.StatusCode) ? HttpStatusCode.Created : HttpStatusCode.OK);
        var view = await Json(response);
        view.GetProperty("accountShared").GetBoolean().Should().BeTrue();
        view.GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();
        foreach (var (method, url, body) in new (HttpMethod, string, object)[]
        {
            (HttpMethod.Put, $"{Donors}/{id}/login/password", new { password = Own }),
            (HttpMethod.Put, $"{Donors}/{id}/login/status", new { status = "disabled" })
        })
        {
            using var refused = await Send(method, url, admin, body);
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, url);
            (await ErrorCode(refused)).Should().Be("ACCOUNT_SHARED");
        }

        using var memberLogin = await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "member@example.org", password = "Member-Own-Pass-77" });
        memberLogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnEmailUsedByAStaffAccountCannotBecomeADonorSignIn()
    {
        await CreateAuthUser("staffer@example.org", KjRoles.ContentEditor, Initial);
        var id = await Create("staffer@example.org");
        var admin = await Staff("admin-staff@example.org");

        using var response = await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(response)).Should().Be("EMAIL_TAKEN");
        (await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id))).AccountSubject.Should().BeNull();
    }

    [Fact]
    public async Task ANewInitialPasswordMakesTheDonorChooseAgainAndEndsTheirSessions()
    {
        var id = await Create("reset@example.org");
        var admin = await Staff("admin-reset@example.org");
        await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial });
        using var first = await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "reset@example.org", password = Initial });
        var access = (await Json(first)).GetProperty("access_token").GetString()!;
        using var changed = await SendAuth(HttpMethod.Post, "/api/v1/auth/change-password", access, new { currentPassword = Initial, newPassword = Own });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        var refresh = (await Json(changed)).GetProperty("refresh_token").GetString();
        (await Get(id, admin)).GetProperty("profile").GetProperty("mustChangePassword").GetBoolean().Should().BeFalse("read live: the donor already chose their own");

        using var reset = await Send(HttpMethod.Put, $"{Donors}/{id}/login/password", admin, new { password = "Replacement-Pass-4" });

        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        (await reset.Content.ReadAsStringAsync()).Should().NotContain("Replacement-Pass-4");
        (await Json(reset)).GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        (await Get(id, admin)).GetProperty("profile").GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        (await _authClient.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Put, $"{Donors}/{id}/login/password", admin, new { password = "short" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DisablingAndReEnablingTheSignInWorksThroughIdentityAndAnInactiveDonorCannotBeReEnabled()
    {
        var id = await Create("switch@example.org");
        var admin = await Staff("admin-switch@example.org");
        await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial });

        (await Send(HttpMethod.Put, $"{Donors}/{id}/login/status", admin, new { status = "disabled" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id))).AccountStatus.Should().Be("disabled");
        using var login = await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "switch@example.org", password = Initial });
        login.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(login)).Should().Be("ACCOUNT_DISABLED");
        (await Send(HttpMethod.Put, $"{Donors}/{id}/login/status", admin, new { status = "banana" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await Send(HttpMethod.Put, $"{Donors}/{id}/login/status", admin, new { status = "active" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "switch@example.org", password = Initial })).StatusCode.Should().Be(HttpStatusCode.OK);

        var etag = (await Send(HttpMethod.Get, $"{Donors}/{id}", Viewer)).Headers.ETag!.Tag;
        (await Patch(id, etag, new { status = "inactive" }, admin)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var refused = await Send(HttpMethod.Put, $"{Donors}/{id}/login/status", admin, new { status = "active" });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(refused)).Should().Be("DONOR_INACTIVE");
    }

    [Fact]
    public async Task SettingADonorInactiveSwitchesOffADonorOnlySignInAndReactivatingDoesNotSwitchItBackOn()
    {
        var id = await Create("inactive-login@example.org");
        var admin = await Staff("admin-inactive@example.org");
        await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial });

        (await Patch(id, "\"2\"", new { status = "inactive" }, admin)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id))).AccountStatus.Should().Be("disabled");
        (await ErrorCode(await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "inactive-login@example.org", password = Initial }))).Should().Be("ACCOUNT_DISABLED");

        (await Patch(id, "\"3\"", new { status = "active" }, admin)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id))).AccountStatus.Should().Be("disabled", "staff switch the sign-in on again themselves");
        (await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "inactive-login@example.org", password = Initial })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SettingADonorInactiveLeavesASignInSharedWithAMemberAlone()
    {
        await CreateAuthUser("shared@example.org", KjRoles.Member, "Member-Own-Pass-77");
        var id = await Create("shared@example.org");
        var admin = await Staff("admin-shared@example.org");
        await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial });

        (await Patch(id, "\"2\"", new { status = "inactive" }, admin)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id))).Status.Should().Be("inactive");
        (await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id))).AccountStatus.Should().Be("active");
        (await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "shared@example.org", password = "Member-Own-Pass-77" })).StatusCode.Should().Be(HttpStatusCode.OK, "the member keeps their access");
    }

    [Fact]
    public async Task WhenIdentityCannotBeReachedNothingIsSavedAndTheDetailStillLoads()
    {
        await using var offline = new FinanceApiFactory { AuthHandler = new ThrowingHandler() };
        offline.Settings["Finance:Proofs:Path"] = _proofs;
        using var client = offline.CreateClient();
        var id = await CreateOn(client, "offline@example.org");
        var admin = await Staff("admin-offline@example.org");

        using var provision = await SendOn(client, HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial });
        provision.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ErrorCode(provision)).Should().Be("AUTH_UNAVAILABLE");
        using (var scope = offline.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Donors.AsNoTracking().SingleAsync(d => d.Id == id)).AccountSubject.Should().BeNull();
        }

        // A donor that already has an account, set inactive while Identity is down: refused, and the donor stays active.
        using (var scope = offline.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var donor = await db.Donors.SingleAsync(d => d.Id == id);
            donor.AccountSubject = "usr_somebody";
            donor.AccountStatus = "active";
            donor.Version++;
            await db.SaveChangesAsync();
        }

        using var inactive = await SendOn(client, HttpMethod.Patch, $"{Donors}/{id}", Manager, new { status = "inactive" }, ifMatch: "\"2\"");
        inactive.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var detail = await SendOn(client, HttpMethod.Get, $"{Donors}/{id}", Viewer);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = (await Json(detail)).GetProperty("profile");
        profile.GetProperty("status").GetString().Should().Be("active");
        profile.GetProperty("mustChangePassword").ValueKind.Should().Be(JsonValueKind.Null, "unknown is shown as unknown, not guessed");
    }

    [Fact]
    public async Task TheIdentityStateEndpointNeedsFinanceViewAndKnowsOnlyDonorAccounts()
    {
        var id = await Create("state@example.org");
        var admin = await Staff("admin-state@example.org");
        await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Initial });
        var subject = (await Db(db => db.Donors.AsNoTracking().SingleAsync(d => d.Id == id))).AccountSubject!;
        var memberId = await CreateAuthUser("plain-member@example.org", KjRoles.Member, Initial);
        var noPermission = TestTokens.Issue([Permissions.UsersView], subject: "usr_nobody");
        _ = noPermission;

        (await SendAuth(HttpMethod.Get, $"/api/v1/admin/auth/donors/{subject}", admin)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAuth(HttpMethod.Get, $"/api/v1/admin/auth/donors/{memberId}", admin)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAuth(HttpMethod.Get, $"/api/v1/admin/auth/donors/{subject}", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var volunteer = await Staff("volunteer@example.org", KjRoles.Volunteer);
        (await SendAuth(HttpMethod.Get, $"/api/v1/admin/auth/donors/{subject}", volunteer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------- secrets stay out

    [Fact]
    public async Task NoPasswordAppearsInAnyResponseLogOrAuditRow()
    {
        var id = await Create("secret@example.org", address: "A Private Street 9", notes: "Private note text");
        var admin = await Staff("admin-auditor@example.org");
        const string Secret = "Very-Secret-Initial-Pass-9";
        const string Reset = "Another-Secret-Reset-Pass-8";
        var responses = new List<string>();
        foreach (var response in new[]
        {
            await Send(HttpMethod.Post, $"{Donors}/{id}/login", admin, new { password = Secret }),
            await Send(HttpMethod.Put, $"{Donors}/{id}/login/password", admin, new { password = Reset }),
            await Send(HttpMethod.Get, $"{Donors}/{id}", Viewer),
            await Send(HttpMethod.Get, Donors, Viewer)
        })
        {
            responses.Add(await response.Content.ReadAsStringAsync());
            response.Dispose();
        }

        string.Join('\n', responses).Should().NotContain(Secret).And.NotContain(Reset);
        string.Join('\n', _finance.Logs.Lines).Should().NotContain(Secret).And.NotContain(Reset);
        var audit = JsonSerializer.Serialize(await Db(db => db.Set<AuditEvent>().AsNoTracking().ToListAsync()));
        audit.Should().NotContain(Secret).And.NotContain(Reset).And.NotContain("Private Street").And.NotContain("Private note").And.NotContain("secret@example.org");
        (await Db(db => db.Set<AuditEvent>().AsNoTracking().CountAsync(e => e.Entity == "Donor"))).Should().BeGreaterThan(0, "donor changes are audited");
    }

    [Fact]
    public async Task EveryProfileChangeIsAuditedWithoutTheProtectedValues()
    {
        var id = await Create("audited@example.org", address: "Hidden Road 1");
        (await Patch(id, "\"1\"", new { type = "organisation", organisation = "Kolpojontro Friends", address = "Hidden Road 2", status = "inactive" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await Db(db => db.Set<AuditEvent>().AsNoTracking().Where(e => e.Entity == "Donor" && e.EntityId == id).OrderBy(e => e.Id).ToListAsync());

        rows.Select(r => r.Action).Should().Equal("create", "update");
        rows[1].After.Should().Contain("organisation").And.Contain("inactive").And.NotContain("Hidden Road");
        rows[1].Before.Should().Contain("individual").And.Contain("active");
    }

    // ---------------------------------------------------------------- helpers

    private async Task<string> Create(string email, string name = "Donor Name", string? phone = null, string? address = null, string? notes = null)
    {
        using var response = await Send(HttpMethod.Post, Donors, Manager, new { name, email, phone, address, notes });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("donor").GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateOn(HttpClient client, string email)
    {
        using var response = await SendOn(client, HttpMethod.Post, Donors, Manager, new { name = "Donor Name", email });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("donor").GetProperty("id").GetString()!;
    }

    private async Task Gift(string email, string amount)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/donations", new
        {
            amount,
            currency = "BDT",
            frequency = "once",
            designation = new { type = "general" },
            donor = new { name = "A Giver", email },
            anonymous = false,
            coverFees = false,
            language = "en",
            communicationConsent = false
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var id = (await Json(response)).GetProperty("id").GetString()!;
        using var scope = _finance.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<DonationConfirmation>().ConfirmAsync(id, "bkash", null)).IsSuccess.Should().BeTrue();
    }

    private async Task<JsonElement> List(string query = "")
    {
        using var response = await Send(HttpMethod.Get, Donors + query, Viewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("items");
    }

    private async Task<JsonElement> Get(string id, string? token = null)
    {
        using var response = await Send(HttpMethod.Get, $"{Donors}/{id}", token ?? Viewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await Json(response);
    }

    private Task<HttpResponseMessage> Patch(string id, string ifMatch, object body, string? token = null) =>
        Send(HttpMethod.Patch, $"{Donors}/{id}", token ?? Manager, body, ifMatch);

    private async Task<T> Db<T>(Func<FinanceDbContext, Task<T>> action)
    {
        using var scope = _finance.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<FinanceDbContext>());
    }

    private async Task<string> CreateAuthUser(string email, string role, string password)
    {
        using var scope = _auth.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUserEntity>>();
        var user = new AuthUserEntity { UserName = email, Email = email, EmailConfirmed = true, Status = "active", CreatedAt = DateTimeOffset.UtcNow };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        await users.AddToRoleAsync(user, role);
        return user.Id;
    }

    /// <summary>A real staff token from Identity, so Finance's calls to Identity carry a token Identity itself accepts.</summary>
    private async Task<string> Staff(string email, string role = KjRoles.Admin)
    {
        await CreateAuthUser(email, role, Initial);
        using var login = await _authClient.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Initial });
        login.StatusCode.Should().Be(HttpStatusCode.OK, email);
        return (await Json(login)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token, object? body = null, string? ifMatch = null) =>
        SendOn(_client, method, url, token, body, ifMatch);

    private Task<HttpResponseMessage> SendAuth(HttpMethod method, string url, string? token, object? body = null) =>
        SendOn(_authClient, method, url, token, body);

    private static async Task<HttpResponseMessage> SendOn(HttpClient client, HttpMethod method, string url, string? token, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
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

        return await TestControls.SendAsync(client, request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("error").GetProperty("code").GetString();

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Identity is down.");
    }
}
