using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FinanceService.Donors;

public enum AuthOutcome
{
    Ok,
    Created,
    Invalid,
    EmailTaken,
    Shared,
    NotFound,
    Forbidden,
    Unavailable
}

/// <summary>What Identity answered. `Message` is Identity's own description of an invalid field and never contains a password.</summary>
public sealed record DonorAccountResult(AuthOutcome Outcome, string? UserId = null, bool LinkedToMember = false, string? Message = null)
{
    public bool Succeeded => Outcome is AuthOutcome.Ok or AuthOutcome.Created;
}

/// <summary>An account as Identity holds it now: the donor detail shows this, so it cannot go stale when the donor changes their own password.</summary>
public sealed record DonorAccountState(string Status, bool MustChangePassword, bool Shared);

public interface IAuthDonorClient
{
    Task<DonorAccountResult> ProvisionAsync(string donorId, string email, string password, string? bearerToken, CancellationToken cancellationToken);

    Task<DonorAccountResult> SetPasswordAsync(string userId, string password, string? bearerToken, CancellationToken cancellationToken);

    Task<DonorAccountResult> SetStatusAsync(string userId, bool enabled, string? bearerToken, CancellationToken cancellationToken);

    /// <summary>Null when Identity cannot answer: the caller shows "unknown" rather than a guess.</summary>
    Task<DonorAccountState?> GetAsync(string userId, string? bearerToken, CancellationToken cancellationToken);
}

/// <summary>
/// Calls Identity as the staff member: their own access token is forwarded, so Identity decides whether they may provision donor
/// accounts (`finance.donor.provision`). Service-to-service credentials wait for BE-093. A request body holds a password, so it is
/// never logged, and neither is a response.
/// </summary>
public sealed partial class AuthDonorClient(IHttpClientFactory factory, ILogger<AuthDonorClient> logger) : IAuthDonorClient
{
    public const string ClientName = "auth";

    public Task<DonorAccountResult> ProvisionAsync(string donorId, string email, string password, string? bearerToken, CancellationToken cancellationToken) =>
        SendAsync("provision", HttpMethod.Post, "/api/v1/admin/auth/donors", new { donorId, email, password }, bearerToken, cancellationToken);

    public Task<DonorAccountResult> SetPasswordAsync(string userId, string password, string? bearerToken, CancellationToken cancellationToken) =>
        SendAsync("password", HttpMethod.Put, $"/api/v1/admin/auth/donors/{Uri.EscapeDataString(userId)}/password", new { password }, bearerToken, cancellationToken);

    public Task<DonorAccountResult> SetStatusAsync(string userId, bool enabled, string? bearerToken, CancellationToken cancellationToken) =>
        SendAsync("status", HttpMethod.Put, $"/api/v1/admin/auth/donors/{Uri.EscapeDataString(userId)}/status", new { status = enabled ? "active" : "disabled" }, bearerToken, cancellationToken);

    public async Task<DonorAccountState?> GetAsync(string userId, string? bearerToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Build(HttpMethod.Get, $"/api/v1/admin/auth/donors/{Uri.EscapeDataString(userId)}", null, bearerToken);
            using var response = await factory.CreateClient(ClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return new DonorAccountState(
                body.GetProperty("status").GetString() ?? "active",
                body.GetProperty("mustChangePassword").GetBoolean(),
                body.GetProperty("shared").GetBoolean());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            LogUnavailable(logger, "read");
            return null;
        }
    }

    private async Task<DonorAccountResult> SendAsync(string operation, HttpMethod method, string path, object body, string? bearerToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Build(method, path, body, bearerToken);
            using var response = await factory.CreateClient(ClientName).SendAsync(request, cancellationToken);
            var json = await TryReadAsync(response, cancellationToken);
            var code = json?.TryGetProperty("error", out var error) == true && error.TryGetProperty("code", out var c) ? c.GetString() : null;
            var message = json?.TryGetProperty("error", out var e2) == true && e2.TryGetProperty("message", out var m) ? m.GetString() : null;

            if (response.IsSuccessStatusCode)
            {
                var linked = json?.TryGetProperty("linkedToMember", out var l) == true && l.GetBoolean();
                var userId = json?.TryGetProperty("userId", out var u) == true ? u.GetString() : null;
                return new DonorAccountResult(response.StatusCode == HttpStatusCode.Created ? AuthOutcome.Created : AuthOutcome.Ok, userId, linked);
            }

            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest => new DonorAccountResult(AuthOutcome.Invalid, Message: message),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new DonorAccountResult(AuthOutcome.Forbidden),
                HttpStatusCode.NotFound => new DonorAccountResult(AuthOutcome.NotFound),
                HttpStatusCode.Conflict when code == "EMAIL_TAKEN" => new DonorAccountResult(AuthOutcome.EmailTaken),
                HttpStatusCode.Conflict when code == "ACCOUNT_SHARED" => new DonorAccountResult(AuthOutcome.Shared),
                _ => new DonorAccountResult(AuthOutcome.Unavailable)
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            LogUnavailable(logger, operation);
            return new DonorAccountResult(AuthOutcome.Unavailable);
        }
    }

    private static HttpRequestMessage Build(HttpMethod method, string path, object? body, string? bearerToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = string.IsNullOrEmpty(bearerToken) ? null : new AuthenticationHeaderValue("Bearer", bearerToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static async Task<JsonElement?> TryReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identity could not be reached for a donor account call ({Operation}).")]
    private static partial void LogUnavailable(ILogger logger, string operation);
}
