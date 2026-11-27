using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReportingService.Data;
using ReportingService.Data.Entities;

namespace ReportingService.Analytics;

public sealed class AnalyticsOptions
{
    public const string SectionName = "Reporting:Analytics";

    /// <summary>Secret mixed into the visitor hash. Set it in deployment; without it a random one is made at start-up, so hashes do not survive a restart (which is fine: they are never joined to anything).</summary>
    public string? HashSecret { get; set; }

    /// <summary>Most events one hashed visitor may send per minute before the rest are refused with 429.</summary>
    public int MaxEventsPerMinute { get; set; } = 120;

    /// <summary>Events older than this are deleted by the nightly job. 0 keeps everything. The real window is the data-retention decision (#110).</summary>
    public int RetentionDays { get; set; } = 730;

    public int MaxBodyBytes { get; set; } = 2048;
}

/// <summary>The event names the frontend sends (docs/ANALYTICS_EVENTS.md in the frontend repository). They are a contract: add new ones, never rename.</summary>
public static class AnalyticsEvents
{
    public const string ApplicationPageView = "application_page_view";
    public const string ApplicationStarted = "application_started";
    public const string RegistrationSubmitSuccess = "registration_submit_success";
    public const string ApplicationStepView = "application_step_view";
    public const string ApplicationStepComplete = "application_step_complete";
    public const string ApplicationAbandon = "application_abandon";
    public const string DonateView = "donate_view";
    public const string DonateCopy = "donate_copy";
    public const string DonationStepView = "donation_step_view";
    public const string DonationStepComplete = "donation_step_complete";
    public const string DonationAbandon = "donation_abandon";

    public static readonly IReadOnlyList<string> All =
    [
        ApplicationPageView, ApplicationStarted, RegistrationSubmitSuccess, ApplicationStepView, ApplicationStepComplete, ApplicationAbandon,
        DonateView, DonateCopy, DonationStepView, DonationStepComplete, DonationAbandon
    ];

    public static readonly IReadOnlyList<string> ApplicationSteps = ["personal", "education", "address", "motivation", "attachments", "review"];
    public static readonly IReadOnlyList<string> DonationSteps = ["amount", "details", "review"];
}

public enum IngestOutcome { Accepted, Ignored, Invalid, TooMany }

public sealed record IngestResult(IngestOutcome Outcome, string? Message = null);

/// <summary>
/// Server-side event collection. Nothing about a person is kept: no IP, no user agent, no cookie, no free text. The visitor is a hash of IP and user agent under a secret and the
/// day, so it cannot be reversed or followed from one day to the next, and the only values an event can carry are short tokens that match the frontend's own filter.
/// </summary>
public sealed partial class AnalyticsStore(ReportingDbContext db, IOptions<AnalyticsOptions> options, TimeProvider clock)
{
    private static readonly byte[] ProcessSecret = RandomNumberGenerator.GetBytes(32);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Window, int Count)> Recent = new();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,40}$")]
    private static partial Regex Token();

    [GeneratedRegex("^/[A-Za-z0-9_./-]{0,199}$")]
    private static partial Regex SafePath();

    public async Task<IngestResult> IngestAsync(string body, string? ip, string? userAgent, bool optedOut, CancellationToken ct)
    {
        if (optedOut)
        {
            return new IngestResult(IngestOutcome.Ignored);
        }

        string? name;
        Dictionary<string, string> props = [];
        string? path = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new IngestResult(IngestOutcome.Invalid, "The body must be a JSON object.");
            }

            name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (root.TryGetProperty("props", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in p.EnumerateObject())
                {
                    if (prop.Name is not ("step" or "field" or "run"))
                    {
                        return new IngestResult(IngestOutcome.Invalid, $"Property '{prop.Name}' is not accepted.");
                    }

                    var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                    if (value is null || !Token().IsMatch(value))
                    {
                        return new IngestResult(IngestOutcome.Invalid, $"Property '{prop.Name}' must be a short token of letters, digits, dots, dashes and underscores.");
                    }

                    props[prop.Name] = value;
                }
            }

            if (root.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
            {
                path = pathElement.GetString();
            }
        }
        catch (JsonException)
        {
            return new IngestResult(IngestOutcome.Invalid, "The body is not valid JSON.");
        }

        if (name is null || !AnalyticsEvents.All.Contains(name))
        {
            return new IngestResult(IngestOutcome.Invalid, "Unknown event name.");
        }

        props.TryGetValue("step", out var step);
        var steps = name.StartsWith("application_", StringComparison.Ordinal) ? AnalyticsEvents.ApplicationSteps : AnalyticsEvents.DonationSteps;
        var stepEvent = name is AnalyticsEvents.ApplicationStepView or AnalyticsEvents.ApplicationStepComplete or AnalyticsEvents.ApplicationAbandon
            or AnalyticsEvents.DonationStepView or AnalyticsEvents.DonationStepComplete or AnalyticsEvents.DonationAbandon;
        if (stepEvent && (step is null || !steps.Contains(step)))
        {
            return new IngestResult(IngestOutcome.Invalid, $"{name} needs a step from: {string.Join(", ", steps)}.");
        }

        if (!stepEvent && props.ContainsKey("step"))
        {
            return new IngestResult(IngestOutcome.Invalid, $"{name} takes no step.");
        }

        if (path is not null && !SafePath().IsMatch(path))
        {
            path = null;
        }

        var now = clock.GetUtcNow();
        var visitor = Hash(ip, userAgent, now);
        if (TooMany(visitor, now))
        {
            return new IngestResult(IngestOutcome.TooMany);
        }

        db.AnalyticsEvents.Add(new AnalyticsEventEntity
        {
            At = now.UtcDateTime, Name = name, Step = step, Field = props.GetValueOrDefault("field"), Run = props.GetValueOrDefault("run"), Path = path, Visitor = visitor
        });
        await db.SaveChangesAsync(ct);
        return new IngestResult(IngestOutcome.Accepted);
    }

    private string Hash(string? ip, string? userAgent, DateTimeOffset now)
    {
        var secret = options.Value.HashSecret is { Length: > 0 } configured ? Encoding.UTF8.GetBytes(configured) : ProcessSecret;
        var input = Encoding.UTF8.GetBytes($"{now:yyyy-MM-dd}|{ip}|{userAgent}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(secret, input))[..32];
    }

    private bool TooMany(string visitor, DateTimeOffset now)
    {
        var limit = Math.Max(1, options.Value.MaxEventsPerMinute);
        var entry = Recent.AddOrUpdate(visitor, _ => (now, 1), (_, old) => now - old.Window >= TimeSpan.FromMinutes(1) ? (now, 1) : (old.Window, old.Count + 1));
        if (Recent.Count > 10_000)
        {
            foreach (var stale in Recent.Where(r => now - r.Value.Window >= TimeSpan.FromMinutes(1)).Select(r => r.Key).ToList())
            {
                Recent.TryRemove(stale, out _);
            }
        }

        return entry.Count > limit;
    }

    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        if (options.Value.RetentionDays <= 0)
        {
            return 0;
        }

        var cutoff = clock.GetUtcNow().AddDays(-options.Value.RetentionDays).UtcDateTime;
        return await db.AnalyticsEvents.Where(e => e.At < cutoff).ExecuteDeleteAsync(ct);
    }

    public static string Day(DateTimeOffset moment) => moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
