using System.Net;
using System.Net.Http.Headers;

namespace MembershipService.Review;

public enum ProvisioningOutcome
{
    Created,
    Existing,
    EmailTaken,
    Forbidden,
    Failed
}

public sealed record ProvisioningResult(ProvisioningOutcome Outcome, string? UserId = null, string? SetupToken = null);

public sealed record ProvisionAccount(
    string ApplicationId, string Email, string FirstName, string LastName, string? Gender, string? DateOfBirth, string? BloodGroup,
    string Address, string? CityOfResidence, string? CountryOfResidence, string? ReasonForJoining, string? PresentOrganization, string? VolunteeringExperience);

public interface IAuthProvisioningClient
{
    /// <summary>Disables or re-enables a member's login. False when Identity refused or could not be reached.</summary>
    Task<bool> SetAccountStatusAsync(string userId, bool enabled, string? bearerToken, CancellationToken cancellationToken);

    /// <summary>Everything Identity holds about the caller (their own token is forwarded). Null when Identity cannot answer.</summary>
    Task<System.Text.Json.JsonElement?> GetAccountExportAsync(string? bearerToken, CancellationToken cancellationToken);

    Task<ProvisioningResult> ProvisionAsync(ProvisionAccount account, string? bearerToken, CancellationToken cancellationToken);
}

/// <summary>
/// Calls the Identity service as the approver: their own access token is forwarded, so Identity decides whether they may
/// create logins (`membership.provision`). Service-to-service credentials wait for BE-093.
/// </summary>
public sealed partial class AuthProvisioningClient(IHttpClientFactory factory, ILogger<AuthProvisioningClient> logger) : IAuthProvisioningClient
{
    public const string ClientName = "auth";

    public async Task<bool> SetAccountStatusAsync(string userId, bool enabled, string? bearerToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/auth/members/{Uri.EscapeDataString(userId)}/status") { Content = JsonContent.Create(new { status = enabled ? "active" : "disabled" }) };
            request.Headers.Authorization = string.IsNullOrEmpty(bearerToken) ? null : new AuthenticationHeaderValue("Bearer", bearerToken);
            using var response = await factory.CreateClient(ClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogRefused(logger, (int)response.StatusCode);
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogUnreachable(logger, ex.GetType().Name);
            return false;
        }
    }

    public async Task<System.Text.Json.JsonElement?> GetAccountExportAsync(string? bearerToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me/export");
            request.Headers.Authorization = string.IsNullOrEmpty(bearerToken) ? null : new AuthenticationHeaderValue("Bearer", bearerToken);
            using var response = await factory.CreateClient(ClientName).SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogUnreachable(logger, ex.GetType().Name);
            return null;
        }
    }

    public async Task<ProvisioningResult> ProvisionAsync(ProvisionAccount account, string? bearerToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/auth/members") { Content = JsonContent.Create(account) };
            request.Headers.Authorization = string.IsNullOrEmpty(bearerToken) ? null : new AuthenticationHeaderValue("Bearer", bearerToken);
            using var response = await factory.CreateClient(ClientName).SendAsync(request, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Created or HttpStatusCode.OK:
                    var body = await response.Content.ReadFromJsonAsync<AuthProvisioned>(cancellationToken);
                    return body is { UserId.Length: > 0 }
                        ? new ProvisioningResult(response.StatusCode == HttpStatusCode.Created ? ProvisioningOutcome.Created : ProvisioningOutcome.Existing, body.UserId, body.SetupToken)
                        : new ProvisioningResult(ProvisioningOutcome.Failed);
                case HttpStatusCode.Conflict:
                    return new ProvisioningResult(ProvisioningOutcome.EmailTaken);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new ProvisioningResult(ProvisioningOutcome.Forbidden);
                default:
                    LogRefused(logger, (int)response.StatusCode);
                    return new ProvisioningResult(ProvisioningOutcome.Failed);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Never log the account: it is personal data. The status code and exception type are enough to diagnose.
            LogUnreachable(logger, ex.GetType().Name);
            return new ProvisioningResult(ProvisioningOutcome.Failed);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identity refused provisioning with status {Status}")]
    private static partial void LogRefused(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identity could not be reached for provisioning: {Error}")]
    private static partial void LogUnreachable(ILogger logger, string error);

    private sealed record AuthProvisioned(string UserId, string? SetupToken, bool Created);
}
