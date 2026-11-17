using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;

namespace KJWebsite.IntegrationTests;

public sealed class SessionCheckStackTests(PrivacySessionStack stack) : IClassFixture<PrivacySessionStack>
{
    [Fact]
    public async Task RealStaffTokenIsRefusedByFinanceAfterLogoutAll()
    {
        var staff = await stack.NewAdminAsync();
        using var before = await Finance(staff.Token);
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout-all");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staff.Token);
        using var loggedOut = await stack.Gateway.SendAsync(logout);
        loggedOut.EnsureSuccessStatusCode();
        using var after = await Finance(staff.Token);
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private Task<HttpResponseMessage> Finance(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/finance/donors");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return stack.Gateway.SendAsync(request);
    }
}
