using System.Net;
using System.Text.Json;

namespace SponsorshipService.Students;

/// <summary>
/// The projects a programme can point to. Projects belong to the Content service, so the sponsorship tables hold only the project slug
/// and this client asks Content whether it exists and what it is called. Null means Content could not be reached.
/// </summary>
public interface IProjectCatalogue
{
    Task<IReadOnlyList<ProgrammeRef>?> ListAsync(string language, CancellationToken cancellationToken);

    Task<bool?> ExistsAsync(string slug, CancellationToken cancellationToken);
}

public sealed partial class ProjectCatalogue(IHttpClientFactory factory, ILogger<ProjectCatalogue> logger) : IProjectCatalogue
{
    public const string ClientName = "content";

    public async Task<IReadOnlyList<ProgrammeRef>?> ListAsync(string language, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await factory.CreateClient(ClientName)
                .GetAsync($"/api/v1/content/projects?status=all&limit=100&lang={Uri.EscapeDataString(language)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogRefused(logger, (int)response.StatusCode);
                return null;
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return [.. json.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => new ProgrammeRef(item.GetProperty("slug").GetString()!, item.GetProperty("title").GetString() ?? item.GetProperty("slug").GetString()!))];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            LogUnreachable(logger, ex.GetType().Name);
            return null;
        }
    }

    public async Task<bool?> ExistsAsync(string slug, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await factory.CreateClient(ClientName).GetAsync($"/api/v1/content/projects/{Uri.EscapeDataString(slug)}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            LogRefused(logger, (int)response.StatusCode);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogUnreachable(logger, ex.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Content answered the project lookup with status {Status}.")]
    private static partial void LogRefused(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Content could not be reached for the project lookup ({Reason}).")]
    private static partial void LogUnreachable(ILogger logger, string reason);
}

/// <summary>Project names for display, remembered briefly so a student list does not ask Content on every row.</summary>
public sealed class ProgrammeNames(IProjectCatalogue catalogue, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, Dictionary<string, string> Names)> _cache = [];

    public async Task<Dictionary<string, string>> NamesAsync(string language, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(language, out var hit) && clock.GetUtcNow() - hit.At < Lifetime)
            {
                return hit.Names;
            }
        }

        var list = await catalogue.ListAsync(language, cancellationToken);
        var names = list is null ? [] : list.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        if (list is not null)
        {
            lock (_gate)
            {
                _cache[language] = (clock.GetUtcNow(), names);
            }
        }

        return names;
    }

    public async Task<ProgrammeRef> RefAsync(string id, string language, CancellationToken cancellationToken)
    {
        var names = await NamesAsync(language, cancellationToken);
        return new ProgrammeRef(id, names.TryGetValue(id, out var name) ? name : id);
    }
}
