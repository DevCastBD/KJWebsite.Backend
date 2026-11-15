using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SponsorshipService.Links;

public enum FinanceOutcome { Ok, NotFound, Forbidden, Unavailable }
public sealed record FinanceTarget(string Id, string? AccountSubject, string Status, string AccountStatus);
public sealed record FinanceMe(string Id, string Language = "en");
public sealed record FinanceResult<T>(FinanceOutcome Outcome, T? Value = default, string? Code = null, string? Message = null);
public interface IFinanceDonors
{
    Task<FinanceResult<FinanceTarget>> TargetAsync(string donorId, string bearer, CancellationToken ct);
    Task<FinanceResult<string>> NotifyAsync(string donorId, string linkId, string bearer, CancellationToken ct);
    Task<FinanceResult<FinanceMe>> MeAsync(string bearer, CancellationToken ct);
}

public sealed partial class FinanceDonors(IHttpClientFactory factory, ILogger<FinanceDonors> logger) : IFinanceDonors
{
    public const string ClientName = "finance";
    public Task<FinanceResult<FinanceTarget>> TargetAsync(string donorId, string bearer, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"/api/v1/admin/finance/donors/{Uri.EscapeDataString(donorId)}/link-target", bearer, null,
            j => new FinanceTarget(Required(j, "id"), j.GetProperty("accountSubject").GetString(), Required(j, "status"), Required(j, "accountStatus")), false, ct);
    public Task<FinanceResult<string>> NotifyAsync(string donorId, string linkId, string bearer, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/v1/admin/finance/donors/{Uri.EscapeDataString(donorId)}/link-notices", bearer, linkId,
            j => Required(j, "outcome") is "queued" or "suppressed" ? Required(j, "outcome") : throw new JsonException(), false, ct);
    public Task<FinanceResult<FinanceMe>> MeAsync(string bearer, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "/api/v1/donors/me/identity", bearer, null,
            j => new FinanceMe(Required(j, "id"), j.TryGetProperty("language", out var lang) ? lang.GetString() ?? "en" : "en"), true, ct);

    private static string Required(JsonElement json, string key) => json.GetProperty(key).GetString() is { Length: > 0 } value ? value : throw new JsonException();

    private async Task<FinanceResult<T>> SendAsync<T>(HttpMethod method, string path, string bearer, string? linkId, Func<JsonElement, T> parse, bool self, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (linkId is not null)
            {
                request.Headers.Add("Idempotency-Key", linkId);
                request.Content = JsonContent.Create(new { linkId });
            }
            using var response = await factory.CreateClient(ClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                return new(FinanceOutcome.Ok, parse(json));
            }
            if (!self && response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(FinanceOutcome.NotFound);
            }
            if (self && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                var code = Required(json.GetProperty("error"), "code");
                if ((response.StatusCode == HttpStatusCode.NotFound && code == "NOT_A_DONOR") ||
                    (response.StatusCode == HttpStatusCode.Forbidden && code is "DONOR_INACTIVE" or "ACCOUNT_DISABLED"))
                {
                    return new(response.StatusCode == HttpStatusCode.NotFound ? FinanceOutcome.NotFound : FinanceOutcome.Forbidden, Code: code, Message: json.TryGetProperty("detail", out var detail) ? detail.GetString() : code);
                }
            }
            LogUnavailable(logger);
            return new(FinanceOutcome.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            LogUnavailable(logger);
            return new(FinanceOutcome.Unavailable);
        }
    }
    [LoggerMessage(Level = LogLevel.Warning, Message = "Finance could not complete a donor lookup or notice call.")]
    private static partial void LogUnavailable(ILogger logger);
}
