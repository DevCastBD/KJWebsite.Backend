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
    Task<ProvisioningResult> ProvisionAsync(ProvisionAccount account, string? bearerToken, CancellationToken cancellationToken);
}

/// <summary>
/// Calls the Identity service as the approver: their own access token is forwarded, so Identity decides whether they may
/// create logins (`membership.provision`). Service-to-service credentials wait for BE-093.
/// </summary>
public sealed partial class AuthProvisioningClient(IHttpClientFactory factory, ILogger<AuthProvisioningClient> logger) : IAuthProvisioningClient
{
    public const string ClientName = "auth";

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
