using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AuthIdentityService.Identity;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using static KJWebsite.IntegrationTests.PrivacyFlows;

namespace KJWebsite.IntegrationTests;

#pragma warning disable CA1848, CA1873 // Exercise the provider with ordinary log calls, including exception formatting.
public sealed class PrivacyHarnessLogSinkTests
{
    [Theory]
    [InlineData("message")]
    [InlineData("exception")]
    [InlineData("scope")]
    public void DetectsSecretsWhereverTheyAreWritten(string location)
    {
        var sink = new LogSink();
        sink.Register("email", "Secret-Value-12345");
        var logger = sink.Provider.CreateLogger("privacy.category");
        using var scope = logger.BeginScope(location == "scope" ? "SECRET-VALUE-12345" : "clean scope");
        logger.LogInformation(location == "exception" ? new InvalidOperationException("SECRET-VALUE-12345") : null,
            "{Text}", location == "message" ? "SECRET-VALUE-12345" : "clean message");
        sink.Violations.Should().ContainSingle().Which.Should().Be(new LeakRecord("privacy.category", "email", "Secret-Value-12345"));
        Action check = sink.AssertNone;
        check.Should().Throw<InvalidOperationException>().WithMessage("*logger 'privacy.category' logged 'Secret-Value-12345' (email)*");
        sink.AssertNone();
    }
    [Fact]
    public void CleanLogsAndLateRegistrationDoNotMatchAndShortSeedsAreRefused()
    {
        var sink = new LogSink();
        var logger = sink.Provider.CreateLogger("clean");
        logger.LogInformation("Secret-Value-12345");
        sink.Register("seed", "Secret-Value-12345");
        logger.LogInformation("clean");
        sink.Violations.Should().BeEmpty();
        Action register = () => sink.Register("short", "12345678901");
        register.Should().Throw<ArgumentException>();
        sink.AssertNone();
    }
    [Fact]
    public async Task DevelopmentSenderDoesNotLogAddressOrToken()
    {
        var sink = new LogSink();
        sink.Register("email", "private-address@example.org");
        sink.Register("token", "private-confirmation-token");
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(sink.Provider));
        var sender = new InMemoryAuthEmailSender(logging.CreateLogger<InMemoryAuthEmailSender>(), true);
        var message = new AuthEmail(AuthEmailKind.ConfirmEmail, "usr_test", "private-address@example.org", "private-confirmation-token");
        await sender.SendAsync(message);
        sender.Sent.Should().ContainSingle().Which.Should().Be(message);
        sink.Violations.Should().BeEmpty();
    }
    [Fact]
    public void ConcurrentWritesAndProviderDisposalPreserveDetection()
    {
        var sink = new LogSink();
        sink.Register("seed", "Secret-Value-12345");
        sink.Provider.Dispose();
        Parallel.For(0, 100, _ => sink.Provider.CreateLogger("parallel").LogWarning("Secret-Value-12345"));
        sink.Violations.Should().HaveCount(100);
    }
}

public sealed class PrivacyHarnessTests(PrivacyStack stack) : IClassFixture<PrivacyStack>
{
    private const string Donors = "/api/v1/admin/finance/donors";
    private const string Students = "/api/v1/admin/sponsorship/students";
    private static string DonorToken => TestTokens.Issue([], extraClaims: [new Claim(KjClaims.Role, KjRoles.Donor)]);

    [Theory]
    [InlineData("/api/v1/content/projects", "content", false, 200)]
    [InlineData("/api/v1/donors/me", "finance", true, 404)]
    [InlineData("/api/v1/donors/me/students", "sponsorship", true, 404)]
    public async Task GatewayReachesOneCluster(string path, string cluster, bool donor, int status)
    {
        stack.ResetCalls();
        using var response = await Send(stack, HttpMethod.Get, path, donor ? DonorToken : null);
        ((int)response.StatusCode).Should().Be(status);
        stack.Calls(cluster).Should().Be(1);
        foreach (var other in new[] { "content", "cta", "auth", "membership", "finance", "sponsorship" }.Except([cluster])) stack.Calls(other).Should().Be(0);
    }
    [Fact]
    public async Task LoginReachesIdentity()
    {
        stack.ResetCalls();
        using var response = await Send(stack, HttpMethod.Post, "/api/v1/auth/login", body: new { email = "unknown@example.org", password = "UnknownPassword123" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        stack.Calls("auth").Should().Be(1);
    }
    [Theory]
    [InlineData("/api/v1/donors/me/identity")]
    [InlineData("/.well-known/jwks.json")]
    public async Task InternalPathsReachNoCluster(string path)
    {
        stack.ResetCalls();
        using var response = await Send(stack, HttpMethod.Get, path, DonorToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var cluster in new[] { "content", "cta", "auth", "membership", "finance", "sponsorship" }) stack.Calls(cluster).Should().Be(0);
    }
    [Fact]
    public async Task ExpiredAndMalformedTokensAreRejectedForAuthentication()
    {
        foreach (var token in TestTokens.Malformed().Prepend(TestTokens.Expired([Permissions.ContentView])))
        {
            using var response = await Send(stack, HttpMethod.Get, "/api/v1/admin/content/projects", token);
            var json = await Json(response, HttpStatusCode.Unauthorized);
            json.GetProperty("error").GetProperty("code").GetString().Should().Be("UNAUTHORIZED");
        }
        using var control = await Send(stack, HttpMethod.Get, "/api/v1/admin/content/projects", TestTokens.ContentAdmin());
        control.StatusCode.Should().Be(HttpStatusCode.OK);
    }
    [Fact]
    public async Task DevelopmentEmailKeepsMessageWithoutLoggingAddressOrToken()
    {
        var email = stack.Seed("registration email", $"{stack.NewSeed("register")}@example.org");
        var password = stack.NewSeed("Password9");
        using var response = await Send(stack, HttpMethod.Post, "/api/v1/auth/register", body: new { email, password });
        await Json(response, HttpStatusCode.Created);
        var sender = (InMemoryAuthEmailSender)stack.Auth.Services.GetRequiredService<IAuthEmailSender>();
        sender.Sent.Should().ContainSingle(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Token));
        GlobalLogSink.Default.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task FinanceProvisionsARealIdentitySession()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        (await stack.LoginAsync(donor.Email, donor.Password)).Should().NotBeNullOrEmpty();
        GlobalLogSink.Default.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task SponsorshipUsesRealFinanceLinkTargetAndNotices()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        await LinkStudent(stack, staff.Token, donor.Id);
        GlobalLogSink.Default.Violations.Should().BeEmpty();
    }
    [Fact]
    public async Task FinanceChecksRealSponsorshipBeforeAcceptingStudentGift()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        using var gift = await Send(stack, HttpMethod.Post, "/api/v1/admin/finance/donations", staff.Token,
            new { donorId = donor.Id, amount = "500.00", currency = "BDT", method = "cash", designation = "student", designationId = student });
        await Json(gift, HttpStatusCode.Created);
        GlobalLogSink.Default.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task PrivateAdminResponsesCoverSuccessAndAuthorizationRefusals()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        using var gift = await Send(stack, HttpMethod.Post, "/api/v1/admin/finance/donations", staff.Token,
            new { donorId = donor.Id, amount = "500.00", currency = "BDT", method = "cash", designation = "general" });
        var giftId = (await Json(gift, HttpStatusCode.Created)).GetProperty("id").GetString();
        using var proofRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/finance/donations/{giftId}/proofs")
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4 privacy proof"))
        };
        proofRequest.Content.Headers.ContentType = new("application/pdf");
        proofRequest.Headers.Authorization = new("Bearer", staff.Token);
        using var proofResponse = await stack.Gateway.SendAsync(proofRequest);
        var proofId = (await Json(proofResponse, HttpStatusCode.Created)).GetProperty("id").GetString();
        using var scope = stack.Auth.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AuthIdentityService.Data.Entities.AuthUserEntity>>();
        var user = await users.FindByEmailAsync(donor.Email);
        var realDonorToken = await stack.LoginAsync(donor.Email, donor.Password);
        var paths = new[] { Donors, $"{Donors}/{donor.Id}", $"{Donors}/{donor.Id}/donations", $"{Donors}/{donor.Id}/link-target",
            "/api/v1/admin/finance/donations", $"/api/v1/admin/finance/donations/{giftId}", $"/api/v1/admin/auth/donors/{user!.Id}", $"{Donors}/export?consent=updates", $"/api/v1/admin/finance/donations/{giftId}/proofs/{proofId}/file" };
        foreach (var path in paths)
        {
            foreach (var (token, status) in new[] { (staff.Token, HttpStatusCode.OK), ((string?)null, HttpStatusCode.Unauthorized), (realDonorToken, HttpStatusCode.Forbidden) })
            {
                using var response = await Send(stack, HttpMethod.Get, path, token);
                response.StatusCode.Should().Be(status, path);
                Private(response);
            }
        }
    }
}
