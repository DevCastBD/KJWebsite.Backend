using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuthIdentityService.Data;
using ContentService.Data;
using CtaSubmissionService.Data;
using FinanceService.Data;
using MembershipService.Data;
using SponsorshipService.Data;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using static KJWebsite.IntegrationTests.PrivacyFlows;

namespace KJWebsite.IntegrationTests;

public sealed class PrivacyEndToEndTests(PrivacyStack stack) : IClassFixture<PrivacyStack>
{
    private const string Mine = "/api/v1/donors/me/students";

    [Fact]
    public async Task T03CrossDonorStudentsAreIndistinguishableFromUnknownAndOwnershipCannotBeInjected()
    {
        var staff = await stack.NewAdminAsync();
        var a = await CreateDonor(stack, staff.Token);
        var b = await CreateDonor(stack, staff.Token);
        var studentA = await LinkStudent(stack, staff.Token, a.Id);
        var studentB = await LinkStudent(stack, staff.Token, b.Id);
        await UploadPhoto(stack, staff.Token, studentA);
        await UploadPhoto(stack, staff.Token, studentB);
        var token = await ChoosePassword(a.Email, a.Password);
        var tokenB = await ChoosePassword(b.Email, b.Password);
        var unknown = $"stu_{Guid.NewGuid():N}";

        using var visibleToB = await Send(stack, HttpMethod.Get, $"{Mine}/{studentB}", tokenB);
        await Json(visibleToB, HttpStatusCode.OK);
        using var photoB = await Send(stack, HttpMethod.Get, $"{Mine}/{studentB}/photo", tokenB);
        photoB.StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var suffix in new[] { "", "/photo" })
        {
            var path = $"{Mine}/{studentB}{suffix}";
            using var inaccessible = await Send(stack, HttpMethod.Get, path, token);
            using var missing = await Send(stack, HttpMethod.Get, $"{Mine}/{unknown}{suffix}", token);
            await IdenticalNotFound(inaccessible, missing, path, studentB);
        }

        using var list = await Send(stack, HttpMethod.Get, Mine, token);
        var baseline = await Json(list, HttpStatusCode.OK);
        baseline.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()).Should().Equal(studentA);
        foreach (var path in new[] { Mine, $"{Mine}/{studentA}", $"{Mine}/{studentA}/photo", "/api/v1/donors/me", "/api/v1/donors/me/donations" })
        {
            using var original = await Send(stack, HttpMethod.Get, path, token);
            original.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path}");
            foreach (var body in new object?[] { null, new { donorId = b.Id, studentId = studentB, id = studentB } })
            {
                using var injected = await Send(stack, HttpMethod.Get, $"{path}?donorId={b.Id}&studentId={studentB}&id={studentB}", token, body);
                injected.StatusCode.Should().Be(original.StatusCode, $"GET {path}");
                (await injected.Content.ReadAsByteArrayAsync()).Should().Equal(await original.Content.ReadAsByteArrayAsync(),
                    $"GET {path} leaked '{studentB}' (cross-donor ownership)");
            }
        }
    }

    private const string Students = "/api/v1/admin/sponsorship/students";
    private const string Donors = "/api/v1/admin/finance/donors";

    [Fact]
    public async Task T04PhotoRequiresConsentLinkAndStaffPermission()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        await UploadPhoto(stack, staff.Token, student);
        var token = await ChoosePassword(donor.Email, donor.Password);
        var other = await CreateDonor(stack, staff.Token);
        var otherToken = await ChoosePassword(other.Email, other.Password);
        var path = $"{Mine}/{student}/photo";
        using (var ok = await Send(stack, HttpMethod.Get, path, token)) ok.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var unlinked = await Send(stack, HttpMethod.Get, path, otherToken))
        using (var unknown = await Send(stack, HttpMethod.Get, $"{Mine}/stu_{Guid.NewGuid():N}/photo", otherToken))
            await IdenticalNotFound(unlinked, unknown, path, student);
        using (var revoke = await Send(stack, HttpMethod.Post, $"{Students}/{student}/consents", staff.Token, new { granted = false }))
            await Json(revoke, HttpStatusCode.Created);
        using (var hidden = await Send(stack, HttpMethod.Get, path, token))
        using (var unknown = await Send(stack, HttpMethod.Get, $"{Mine}/stu_{Guid.NewGuid():N}/photo", token))
            await IdenticalNotFound(hidden, unknown, path, student);
        var low = await stack.NewUserAsync(KjRoles.Volunteer);
        foreach (var (who, status) in new[] { (staff.Token, HttpStatusCode.OK), (low.Token, HttpStatusCode.Forbidden), (token, HttpStatusCode.Forbidden) })
        {
            using var photo = await Send(stack, HttpMethod.Get, $"{Students}/{student}/photo", who);
            photo.StatusCode.Should().Be(status, $"GET {Students}/{student}/photo staff permission");
        }
    }

    private string[] DonorPaths(string student, bool includeInternal = false, bool includeRequiredQuery = false) => new[] { stack.Finance.Services, stack.Sponsorship.Services }
        .SelectMany(services => services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
        .Select(e => e.RoutePattern.RawText!)
        .Where(path => path.StartsWith("/api/v1/donors/me", StringComparison.Ordinal) && (includeInternal || !path.EndsWith("/identity", StringComparison.Ordinal)))
        .Select(path => path.Replace("{studentId}", student, StringComparison.Ordinal))
        .Select(path => includeRequiredQuery && path == $"{Mine}/names" ? $"{path}?ids={student}" : path)
        .Distinct().Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task T05EveryDonorSelfPathRejectsWritesWithoutChanges()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        await UploadPhoto(stack, staff.Token, student);
        var token = await ChoosePassword(donor.Email, donor.Password);
        var paths = DonorPaths(student, true);
        paths.Should().NotBeEmpty();
        var before = await BusinessRows();
        foreach (var path in paths)
        {
            using var original = await Send(stack, HttpMethod.Get, path, token);
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            {
                using var response = await Send(stack, method, path, token, new { name = stack.NewSeed("write-attempt") });
                // The internal identity lookup is deliberately absent from the gateway.
                var expected = path.EndsWith("/identity", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.MethodNotAllowed;
                response.StatusCode.Should().Be(expected, $"{method} {path} must reject donor writes");
                using var after = await Send(stack, HttpMethod.Get, path, token);
                after.StatusCode.Should().Be(original.StatusCode);
                (await after.Content.ReadAsByteArrayAsync()).Should().Equal(await original.Content.ReadAsByteArrayAsync(), $"{method} {path} changed data");
            }
        }
        (await BusinessRows()).Should().Be(before, "donor writes must change no business rows");
    }

    [Fact]
    public async Task T06EndedSponsorshipKeepsFrozenFieldsAfterStaffEdits()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        await UploadPhoto(stack, staff.Token, student);
        await PatchOk($"{Students}/{student}", staff.Token, new { storyEn = stack.NewSeed("original-story") });
        var token = await ChoosePassword(donor.Email, donor.Password);
        using var active = await Send(stack, HttpMethod.Get, $"{Mine}/{student}", token);
        var activeBody = JsonNode.Parse(await active.Content.ReadAsStringAsync())!.AsObject();
        using var ended = await Unlink(staff.Token, donor.Id, student, "ended");
        await Json(ended, HttpStatusCode.OK);
        using var frozen = await Send(stack, HttpMethod.Get, $"{Mine}/{student}", token);
        var frozenBody = await Json(frozen, HttpStatusCode.OK);
        frozenBody.GetProperty("sponsorship").GetString().Should().Be("ended");
        frozenBody.GetProperty("endedOn").GetString().Should().NotBeNullOrEmpty();
        var expected = JsonNode.Parse(frozenBody.GetRawText())!.AsObject();
        activeBody.Remove("sponsorship"); activeBody.Remove("endedOn");
        var fields = expected.DeepClone().AsObject(); fields.Remove("sponsorship"); fields.Remove("endedOn");
        JsonNode.DeepEquals(activeBody, fields).Should().BeTrue("ending must freeze the last shared fields");
        await PatchOk($"{Students}/{student}", staff.Token, new { preferredName = stack.NewSeed("later-name"), school = stack.NewSeed("later-school"), storyEn = stack.NewSeed("later-story"), district = "Chattogram" });
        using var after = await Send(stack, HttpMethod.Get, $"{Mine}/{student}", token);
        (await after.Content.ReadAsStringAsync()).Should().Be(frozenBody.GetRawText(), $"GET {Mine}/{student} must retain frozen fields");
        using var list = await Send(stack, HttpMethod.Get, Mine, token);
        var item = (await Json(list, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == student);
        item.GetRawText().Should().Be(frozenBody.GetRawText());
    }

    [Fact]
    public async Task T11MistakenLinkDisappearsForDonorWithoutFrozenCopyButRemainsForStaff()
    {
        var staff = await stack.NewSuperAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        var token = await ChoosePassword(donor.Email, donor.Password);
        using var removed = await Unlink(staff.Token, donor.Id, student, "mistaken");
        await Json(removed, HttpStatusCode.OK);
        foreach (var suffix in new[] { "", "/photo" })
        {
            using var hidden = await Send(stack, HttpMethod.Get, $"{Mine}/{student}{suffix}", token);
            using var unknown = await Send(stack, HttpMethod.Get, $"{Mine}/stu_{Guid.NewGuid():N}{suffix}", token);
            await IdenticalNotFound(hidden, unknown, $"{Mine}/{student}{suffix}", student);
        }
        using var list = await Send(stack, HttpMethod.Get, Mine, token);
        (await Json(list, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Should().BeEmpty();
        using var history = await Send(stack, HttpMethod.Get, $"/api/v1/admin/sponsorship/donors/{donor.Id}/students?status=all", staff.Token);
        (await Json(history, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Should().ContainSingle().Which.GetProperty("endType").GetString().Should().Be("mistaken");
        using var scope = stack.Sponsorship.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SponsorshipDbContext>();
        (await db.FrozenCopies.AnyAsync(x => x.StudentId == student)).Should().BeFalse("mistaken links never create a frozen copy");
    }

    [Fact]
    public async Task T24OnlySuperAdminCanVoidAMistakenLinkAcrossAllSeededRoles()
    {
        var staff = await stack.NewSuperAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        foreach (var role in KjRoles.Seed.Keys.Where(role => role != KjRoles.SuperAdmin).OrderBy(role => role, StringComparer.Ordinal))
        {
            var user = await stack.NewUserAsync(role);
            var before = await BusinessRows();
            using var response = await Unlink(user.Token, donor.Id, student, "mistaken");
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"POST /api/v1/admin/sponsorship/donors/{donor.Id}/students/{student}/unlink role {role}");
            (await BusinessRows()).Should().Be(before, $"role {role} must not alter links or frozen copies");
        }
        using var permitted = await Unlink(staff.Token, donor.Id, student, "mistaken");
        (await Json(permitted, HttpStatusCode.OK)).GetProperty("endType").GetString().Should().Be("mistaken");
    }

    [Fact]
    public async Task T25SignedConsentFormRequiresStaffPermission()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        var low = await stack.NewUserAsync(KjRoles.Volunteer);
        var token = await ChoosePassword(donor.Email, donor.Password);
        foreach (var (who, status) in new[] { ((string?)null, HttpStatusCode.Unauthorized), (token, HttpStatusCode.Forbidden), (low.Token, HttpStatusCode.Forbidden), (staff.Token, HttpStatusCode.OK) })
        {
            using var response = await Send(stack, HttpMethod.Get, $"{Students}/{student}/consent-form", who);
            response.StatusCode.Should().Be(status, $"GET {Students}/{student}/consent-form");
            if (status == HttpStatusCode.OK) (await response.Content.ReadAsStringAsync()).Should().StartWith("%PDF");
        }
    }

    [Fact]
    public async Task T26DonorCannotReachSignedFormThroughAnyMappedPath()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        var token = await ChoosePassword(donor.Email, donor.Password);
        var mapped = stack.Sponsorship.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!).Where(p => p.Contains("consent-form", StringComparison.Ordinal))
            .Select(p => p.Replace("{id}", student, StringComparison.Ordinal)).Distinct().ToArray();
        mapped.Should().NotBeEmpty();
        foreach (var path in mapped.Concat(new[] { $"{Mine}/{student}/consent-form", $"/api/v1/donors/me/consent-form", $"{Mine}/{student}/consent-form/file" }))
        {
            using var response = await Send(stack, HttpMethod.Get, path, token);
            response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("%PDF", $"GET {path} leaked signed consent form");
        }
    }

    [Fact]
    public async Task T14FinanceDisableRevokesSameTokenAcrossServicesAndRefresh()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        await UploadPhoto(stack, staff.Token, student);
        var session = await ChooseSession(donor.Email, donor.Password);
        var token = session.GetProperty("access_token").GetString()!;
        var refreshToken = session.GetProperty("refresh_token").GetString();
        var paths = DonorPaths(student, includeRequiredQuery: true).Append("/api/v1/auth/me").ToArray();
        foreach (var path in paths)
        {
            using var baseline = await Send(stack, HttpMethod.Get, path, token);
            baseline.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path} baseline");
        }
        using var disable = await Send(stack, HttpMethod.Put, $"{Donors}/{donor.Id}/login/status", staff.Token, new { status = "disabled" });
        await Json(disable, HttpStatusCode.OK);
        foreach (var path in paths)
        {
            using var response = await Send(stack, HttpMethod.Get, path, token);
            var body = await Json(response, path == "/api/v1/auth/me" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden);
            if (path != "/api/v1/auth/me") body.GetProperty("error").GetProperty("code").GetString().Should().Be("ACCOUNT_DISABLED", $"GET {path}");
        }
        using var refresh = await Send(stack, HttpMethod.Post, "/api/v1/auth/refresh", body: new { refreshToken });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task T22PrivateResponsesReachServiceTwiceAndAreNeverCached()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        await UploadPhoto(stack, staff.Token, student);
        var token = await ChoosePassword(donor.Email, donor.Password);
        using var gift = await Send(stack, HttpMethod.Post, "/api/v1/admin/finance/donations", staff.Token,
            new { donorId = donor.Id, amount = "500.00", currency = "BDT", method = "cash", designation = "student", designationId = student });
        var giftId = (await Json(gift, HttpStatusCode.Created)).GetProperty("id").GetString();
        using var scope = stack.Finance.Services.CreateScope();
        var subject = await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Donors.Where(x => x.Id == donor.Id).Select(x => x.AccountSubject).SingleAsync();
        var paths = DonorPaths(student, includeRequiredQuery: true).Select(p => (Path: p, Token: token, Cluster: p.Contains("/students", StringComparison.Ordinal) ? "sponsorship" : "finance"))
            .Concat(new[] { Students, $"{Students}/{student}", $"{Students}/{student}/photo" }.Select(p => (p, staff.Token, "sponsorship")))
            .Concat(new[] { Donors, $"{Donors}/{donor.Id}", $"{Donors}/{donor.Id}/donations", $"{Donors}/export", "/api/v1/admin/finance/donations", $"/api/v1/admin/finance/donations/{giftId}" }.Select(p => (p, staff.Token, "finance")))
            .Append(($"/api/v1/admin/auth/donors/{subject}", staff.Token, "auth"));
        foreach (var (path, who, cluster) in paths)
        {
            var before = stack.Calls(cluster);
            for (var i = 0; i < 2; i++)
            {
                using var response = await Send(stack, HttpMethod.Get, path, who);
                response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path}");
                response.Headers.CacheControl.Should().NotBeNull($"GET {path} requires cache headers");
                response.Headers.CacheControl!.Private.Should().BeTrue($"GET {path} private");
                response.Headers.CacheControl!.NoStore.Should().BeTrue($"GET {path} no-store");
                if (path.StartsWith("/api/v1/donors/me", StringComparison.Ordinal)) response.Headers.Vary.Should().Contain("Authorization", $"GET {path}");
                response.Headers.Contains("Age").Should().BeFalse($"GET {path}");
            }
            stack.Calls(cluster).Should().Be(before + 2, $"GET {path} must reach {cluster} on both requests");
        }
    }

    [Fact]
    public async Task T27WrongPasswordAndUnknownEmailHaveIdenticalFailures()
    {
        var user = await stack.NewUserAsync(KjRoles.Member);
        var password = stack.NewSeed("WrongPassword9");
        using var wrong = await Send(stack, HttpMethod.Post, "/api/v1/auth/login", body: new { email = user.Email, password });
        using var unknown = await Send(stack, HttpMethod.Post, "/api/v1/auth/login", body: new { email = stack.NewSeed("unknown-email") + "@example.org", password });
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknown.StatusCode.Should().Be(wrong.StatusCode);
        unknown.Content.Headers.ContentType?.ToString().Should().Be(wrong.Content.Headers.ContentType?.ToString());
        (await StableBody(unknown)).Should().Be(await StableBody(wrong), "POST /api/v1/auth/login must not disclose account existence");
    }

    [Fact]
    public async Task T28PasswordProvisionResetAndChangeNeverExposePasswords()
    {
        var staff = await stack.NewAdminAsync();
        var responses = new List<(string Path, string Body)>();
        var donor = await CreateDonor(stack, staff.Token, (path, body) => responses.Add((path, body)));
        var resetPassword = stack.NewSeed("ResetPassword9");
        var finalPassword = stack.NewSeed("ChangedPassword9");
        var passwords = new[] { donor.Password, resetPassword, finalPassword };
        JsonDocument.Parse(responses.Single(x => x.Path == $"POST {Donors}/{donor.Id}/login").Body)
            .RootElement.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        async Task<JsonElement> Capture(HttpMethod method, string path, string? token, object? body, HttpStatusCode status)
        {
            using var response = await Send(stack, method, path, token, body);
            responses.Add(($"{method} {path}", await response.Content.ReadAsStringAsync()));
            return await Json(response, status);
        }
        var initial = await Capture(HttpMethod.Post, "/api/v1/auth/login", null, new { email = donor.Email, password = donor.Password }, HttpStatusCode.OK);
        initial.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        var reset = await Capture(HttpMethod.Put, $"{Donors}/{donor.Id}/login/password", staff.Token, new { password = resetPassword }, HttpStatusCode.OK);
        reset.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        var login = await Capture(HttpMethod.Post, "/api/v1/auth/login", null, new { email = donor.Email, password = resetPassword }, HttpStatusCode.OK);
        login.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        var changed = await Capture(HttpMethod.Post, "/api/v1/auth/change-password", login.GetProperty("access_token").GetString(), new { currentPassword = resetPassword, newPassword = finalPassword }, HttpStatusCode.OK);
        changed.GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();
        var final = await Capture(HttpMethod.Post, "/api/v1/auth/login", null, new { email = donor.Email, password = finalPassword }, HttpStatusCode.OK);
        final.GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();
        foreach (var (path, body) in responses)
            foreach (var password in passwords) body.Should().NotContain(password, $"{path} leaked '{password}' (password)");
        await PasswordsAbsentFromAudit<AuthDbContext>(stack.Auth.Services, "auth", passwords);
        await PasswordsAbsentFromAudit<FinanceDbContext>(stack.Finance.Services, "finance", passwords);
        await PasswordsAbsentFromAudit<SponsorshipDbContext>(stack.Sponsorship.Services, "sponsorship", passwords);
        await PasswordsAbsentFromAudit<ContentDbContext>(stack.Content.Services, "content", passwords);
        await PasswordsAbsentFromAudit<CtaDbContext>(stack.Cta.Services, "cta", passwords);
        await PasswordsAbsentFromAudit<MembershipDbContext>(stack.Membership.Services, "membership", passwords);
        GlobalLogSink.Default.AssertNone();
        GlobalLogSink.Default.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task T29StudentAndDonorPatchRequireCurrentEtagAndRefusalsChangeNothing()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        foreach (var path in new[] { $"{Students}/{student}", $"{Donors}/{donor.Id}" })
        {
            using var first = await Send(stack, HttpMethod.Get, path, staff.Token);
            var stale = first.Headers.ETag!.Tag;
            await PatchOk(path, staff.Token, new { name = stack.NewSeed("current-name") });
            using var current = await Send(stack, HttpMethod.Get, path, staff.Token);
            var before = await current.Content.ReadAsStringAsync();
            var rows = await BusinessRows();
            foreach (var etag in new[] { null, stale })
            {
                using var response = await Patch(path, staff.Token, new { name = stack.NewSeed("refused-name") }, etag);
                response.StatusCode.Should().Be(etag is null ? HttpStatusCode.PreconditionRequired : HttpStatusCode.PreconditionFailed, $"PATCH {path}");
                using var after = await Send(stack, HttpMethod.Get, path, staff.Token);
                (await after.Content.ReadAsStringAsync()).Should().Be(before, $"PATCH {path} refusal must preserve fields");
                after.Headers.ETag!.Tag.Should().Be(current.Headers.ETag!.Tag, $"PATCH {path} refusal must preserve Version");
                (await BusinessRows()).Should().Be(rows, $"PATCH {path} refusal must preserve persisted values and Version");
            }
        }
    }

    private async Task<string> BusinessRows()
    {
        using var finance = stack.Finance.Services.CreateScope();
        using var sponsorship = stack.Sponsorship.Services.CreateScope();
        var f = finance.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var s = sponsorship.ServiceProvider.GetRequiredService<SponsorshipDbContext>();
        return JsonSerializer.Serialize(new
        {
            Donors = await f.Donors.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Students = await s.Students.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Links = await s.Links.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Copies = await s.FrozenCopies.AsNoTracking().OrderBy(x => x.LinkId).ToListAsync(),
            Consents = await s.Consents.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        });
    }

    private static async Task PasswordsAbsentFromAudit<T>(IServiceProvider services, string service, string[] passwords) where T : DbContext
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<T>();
        // Some services, including CTA, have no audit table to scan.
        if (db.Model.FindEntityType(typeof(AuditEvent)) is null) return;
        var rows = await db.Set<AuditEvent>().AsNoTracking().ToListAsync();
        foreach (var row in rows)
            foreach (var password in passwords)
                JsonSerializer.Serialize(row).Should().NotContain(password, $"POST /api/v1/auth/change-password leaked '{password}' ({service} audit row {row.Id})");
    }

    private Task<HttpResponseMessage> Unlink(string token, string donor, string student, string type) =>
        Send(stack, HttpMethod.Post, $"/api/v1/admin/sponsorship/donors/{donor}/students/{student}/unlink", token,
            new { type, endDate = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), reason = stack.NewSeed("unlink-reason") });

    private async Task PatchOk(string path, string token, object body)
    {
        using var current = await Send(stack, HttpMethod.Get, path, token);
        using var response = await Patch(path, token, body, current.Headers.ETag!.Tag);
        await Json(response, HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> Patch(string path, string token, object body, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new("Bearer", token);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await stack.Gateway.SendAsync(request);
    }

    private async Task<string> ChoosePassword(string email, string initial) =>
        (await ChooseSession(email, initial)).GetProperty("access_token").GetString()!;

    private async Task<JsonElement> ChooseSession(string email, string initial)
    {
        var token = await stack.LoginAsync(email, initial);
        using var changed = await Send(stack, HttpMethod.Post, "/api/v1/auth/change-password", token,
            new { currentPassword = initial, newPassword = stack.NewSeed("ChosenPassword9") });
        var session = await Json(changed, HttpStatusCode.OK);
        session.GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();
        return session;
    }

    private static async Task IdenticalNotFound(HttpResponseMessage actual, HttpResponseMessage unknown, string path, string value)
    {
        var message = $"GET {path} leaked '{value}' (existence disclosure)";
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, message);
        actual.StatusCode.Should().Be(HttpStatusCode.NotFound, message);
        actual.Content.Headers.ContentType?.ToString().Should().Be(unknown.Content.Headers.ContentType?.ToString(), message);
        var actualBody = await StableBody(actual);
        var unknownBody = await StableBody(unknown);
        Assert.True(string.Equals(actualBody, unknownBody, StringComparison.Ordinal),
            $"{message}. Bodies must be identical ignoring instance, correlation and trace IDs.\nExpected: {unknownBody}\nActual: {actualBody}");
    }

    private static async Task<string> StableBody(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Normalize(body);
        return body.ToJsonString();
    }

    private static void Normalize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (new[] { "instance", "traceId", "correlationId" }.Contains(key, StringComparer.OrdinalIgnoreCase)) obj.Remove(key);
                else if (obj[key] is { } child) Normalize(child);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array.OfType<JsonNode>()) Normalize(child);
    }
}
