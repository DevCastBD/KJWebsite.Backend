using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FinanceService.Donations;

public enum SponsorshipOutcome { Ok, Unavailable }
public sealed record DesignationCheck(bool Active, string? StudentName);
public sealed record SponsorshipResult<T>(SponsorshipOutcome Outcome, T? Value = default);

public interface ISponsorshipLinks
{
    Task<SponsorshipResult<DesignationCheck>> CheckAsync(string donorId, string studentId, DateOnly receivedOn, string bearer, CancellationToken ct);
    Task<SponsorshipResult<IReadOnlyDictionary<string, string>>> NamesAsync(IReadOnlyCollection<string> studentIds, string bearer, CancellationToken ct);
    Task<SponsorshipResult<IReadOnlyDictionary<string, string>>> DonorNamesAsync(string? bearer, IReadOnlyCollection<string> studentIds, CancellationToken ct);
}

public sealed partial class SponsorshipLinks(IHttpClientFactory factory, ILogger<SponsorshipLinks> logger) : ISponsorshipLinks
{
    public const string ClientName = "sponsorship";
    private const string Route = "/api/v1/admin/sponsorship";

    public Task<SponsorshipResult<DesignationCheck>> CheckAsync(string donorId, string studentId, DateOnly receivedOn, string bearer, CancellationToken ct) =>
        GetAsync($"{Route}/donors/{Uri.EscapeDataString(donorId)}/students/{Uri.EscapeDataString(studentId)}/designation?date={receivedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", bearer,
            j => new DesignationCheck(j.GetProperty("active").GetBoolean(), j.GetProperty("studentName").GetString()), ct);

    public async Task<SponsorshipResult<IReadOnlyDictionary<string, string>>> NamesAsync(IReadOnlyCollection<string> studentIds, string bearer, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var batch in studentIds.Distinct(StringComparer.Ordinal).Chunk(100))
        {
            var result = await GetAsync($"{Route}/students/names?ids={string.Join(",", batch.Select(Uri.EscapeDataString))}", bearer,
                j => j.GetProperty("items").EnumerateArray().ToDictionary(i => Required(i, "id"), i => Required(i, "preferredName"), StringComparer.Ordinal), ct);
            if (result.Outcome != SponsorshipOutcome.Ok) return new(SponsorshipOutcome.Unavailable);
            foreach (var pair in result.Value!) names[pair.Key] = pair.Value;
        }
        return new(SponsorshipOutcome.Ok, names);
    }

    public async Task<SponsorshipResult<IReadOnlyDictionary<string, string>>> DonorNamesAsync(string? bearer, IReadOnlyCollection<string> studentIds, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var batch in studentIds.Distinct(StringComparer.Ordinal).Chunk(100))
        {
            var result = await GetAsync($"/api/v1/donors/me/students/names?ids={string.Join(",", batch.Select(Uri.EscapeDataString))}", bearer,
                j => j.GetProperty("items").EnumerateArray().ToDictionary(i => Required(i, "id"), i => Required(i, "preferredName"), StringComparer.Ordinal), ct);
            if (result.Outcome != SponsorshipOutcome.Ok) return new(SponsorshipOutcome.Unavailable);
            foreach (var pair in result.Value!) names[pair.Key] = pair.Value;
        }
        return new(SponsorshipOutcome.Ok, names);
    }

    private static string Required(JsonElement json, string key) => json.GetProperty(key).GetString() is { Length: > 0 } value ? value : throw new JsonException();

    private async Task<SponsorshipResult<T>> GetAsync<T>(string path, string? bearer, Func<JsonElement, T> parse, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await factory.CreateClient(ClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                return new(SponsorshipOutcome.Ok, parse(json));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            LogUnavailable(logger);
            return new(SponsorshipOutcome.Unavailable);
        }
        LogUnavailable(logger);
        return new(SponsorshipOutcome.Unavailable);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sponsorship could not complete a designation or names lookup.")]
    private static partial void LogUnavailable(ILogger logger);
}
