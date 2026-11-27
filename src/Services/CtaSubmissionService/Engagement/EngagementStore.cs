using CtaSubmissionService.Data;
using CtaSubmissionService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CtaSubmissionService.Engagement;

public static class EngagementCategories
{
    public static readonly string[] All = ["membership", "volunteering", "partnership", "donation", "media", "general"];

    /// <summary>The category an enquiry starts in, from the form's ctaType. A routing rule may change it afterwards.</summary>
    public static string FromCtaType(string ctaType) => ctaType.ToLowerInvariant() switch
    {
        "join" => "membership",
        "get-involved" => "volunteering",
        "partnership" => "partnership",
        "donate" => "donation",
        "media" => "media",
        _ => "general"
    };
}

public static class EngagementPriorities
{
    public static readonly string[] All = ["urgent", "high", "normal"];
    public static int Rank(string priority) => Array.IndexOf(All, priority) is var i and >= 0 ? i : All.Length;
}

public sealed record EngagementRuleWrite(string? Name, string? MatchCategory, string? MatchLanguage, string? MatchKeyword, string? AssignTo, string? Priority, string? SetCategory, bool? Active);
public sealed record EngagementRuleView(string Id, int Position, string Name, string? MatchCategory, string? MatchLanguage, string? MatchKeyword, string? AssignTo, string? Priority, string? SetCategory, bool Active);
public sealed record EngagementRulesRequest(List<EngagementRuleWrite>? Rules);
public sealed record EngagementSlaRequest(Dictionary<string, int?>? Targets);
public sealed record EngagementAssignRequest(string? Assignee);
public sealed record EngagementNoteRequest(string? Note);

public sealed record EngagementItem(string Id, string Category, string Status, string Priority, string? AssignedTo, string FormName, string Language, string SourcePath,
    DateTimeOffset ReceivedAt, DateTimeOffset? FirstResponseAt, DateTimeOffset? ClosedAt, int? SlaTargetMinutes, DateTimeOffset? DueAt, string SlaState, int? MinutesToDue);

public sealed record EngagementDetail(EngagementItem Item, Dictionary<string, object?> Values, List<EngagementEventView> History);
public sealed record EngagementEventView(string Type, string Actor, string? Detail, DateTimeOffset At);
public sealed record EngagementPage(List<EngagementItem> Items, int Total, int Limit, int Offset);

public sealed record EngagementCategoryReport(string Category, int Received, int Responded, int ClosedUnanswered, int Open, int OpenBreached, int OpenAtRisk, int RespondedLate, double? AverageResponseMinutes, double? MedianResponseMinutes, int? TargetMinutes);
public sealed record EngagementReport(DateTimeOffset From, DateTimeOffset To, List<EngagementCategoryReport> Categories);

public sealed record EngagementResult<T>(T? Value, string? Code, string? Error);

public static class EngagementResult
{
    public static EngagementResult<T> Ok<T>(T value) => new(value, null, null);
    public static EngagementResult<T> Fail<T>(string code, string error) => new(default, code, error);
}

/// <summary>
/// One inbox over every kind of enquiry. Intake classifies by form type and applies the first matching routing rule (owner, priority, category); media
/// enquiries are always at least `high`. Response targets are per category and are set by the core members: with none set there is no clock, and no
/// invented number. SLA state is always computed from the current targets, so changing a target takes effect on open enquiries at once.
/// </summary>
public sealed class EngagementStore(CtaDbContext db, TimeProvider time)
{
    private const double AtRiskFraction = 0.25;

    // ---- intake

    /// <summary>Classifies and routes a new submission (called before it is saved) and writes the history rows. Returns the rule that matched, if any.</summary>
    public async Task<string?> RouteAsync(CtaSubmissionEntity submission)
    {
        submission.Category = EngagementCategories.FromCtaType(submission.CtaType);
        var rules = (await db.Set<EngagementRuleEntity>().AsNoTracking().Where(r => r.Active).ToListAsync()).OrderBy(r => r.Position);
        string? matched = null;
        foreach (var rule in rules)
        {
            if ((rule.MatchCategory is null || rule.MatchCategory == submission.Category)
                && (rule.MatchLanguage is null || string.Equals(rule.MatchLanguage, submission.Language, StringComparison.OrdinalIgnoreCase))
                && (rule.MatchKeyword is null || submission.ValuesJson.Contains(rule.MatchKeyword, StringComparison.OrdinalIgnoreCase)))
            {
                submission.Category = rule.SetCategory ?? submission.Category;
                submission.AssignedTo = rule.AssignTo;
                submission.Priority = rule.Priority ?? submission.Priority;
                submission.Status = rule.AssignTo is null ? "new" : "in_progress";
                matched = rule.Name;
                break;
            }
        }

        if (submission.Category == "media" && EngagementPriorities.Rank(submission.Priority) > EngagementPriorities.Rank("high"))
        {
            submission.Priority = "high";
        }

        var now = time.GetUtcNow();
        db.Set<EngagementEventEntity>().Add(new EngagementEventEntity { SubmissionId = submission.Id, Type = "received", Actor = "system", Detail = $"category={submission.Category}", At = now });
        if (matched is not null)
        {
            db.Set<EngagementEventEntity>().Add(new EngagementEventEntity { SubmissionId = submission.Id, Type = "routed", Actor = "system", Detail = $"rule={matched}; assignee={submission.AssignedTo ?? "-"}; priority={submission.Priority}", At = now });
        }

        return matched;
    }

    // ---- inbox

    public async Task<EngagementPage> ListAsync(string? status, string? category, string? assignee, string? slaState, string? me, int limit, int offset)
    {
        var rows = db.Submissions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            rows = status == "open" ? rows.Where(s => s.Status != "closed" && s.Status != "responded") : rows.Where(s => s.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            rows = rows.Where(s => s.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(assignee))
        {
            var who = assignee == "me" ? me : assignee;
            rows = assignee == "none" ? rows.Where(s => s.AssignedTo == null) : rows.Where(s => s.AssignedTo == who);
        }

        var targets = await Targets();
        var now = time.GetUtcNow();
        // Dates are compared in memory: the SQLite test provider cannot order or compare DateTimeOffset in SQL, and an inbox is small.
        var items = (await rows.ToListAsync()).Select(s => ToItem(s, targets, now)).Where(i => string.IsNullOrWhiteSpace(slaState) || i.SlaState == slaState)
            .OrderBy(i => i.Status == "closed" ? 1 : 0).ThenBy(i => EngagementPriorities.Rank(i.Priority)).ThenBy(i => i.DueAt ?? DateTimeOffset.MaxValue).ThenBy(i => i.ReceivedAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        return new EngagementPage([.. items.Skip(offset).Take(limit)], items.Count, limit, offset);
    }

    public async Task<EngagementDetail?> GetAsync(string id)
    {
        var s = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (s is null)
        {
            return null;
        }

        var history = (await db.Set<EngagementEventEntity>().AsNoTracking().Where(e => e.SubmissionId == id).ToListAsync()).OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => new EngagementEventView(e.Type, e.Actor, e.Detail, e.At)).ToList();
        var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(s.ValuesJson) ?? [];
        return new EngagementDetail(ToItem(s, await Targets(), time.GetUtcNow()), values, history);
    }

    public async Task<EngagementResult<EngagementItem>> AssignAsync(string id, string? assignee, string actor)
    {
        var s = await db.Submissions.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null)
        {
            return EngagementResult.Fail<EngagementItem>("NOT_FOUND", "Enquiry not found.");
        }

        var who = assignee == "me" ? actor : assignee?.Trim();
        if (string.IsNullOrEmpty(who) || who.Length > 64)
        {
            return EngagementResult.Fail<EngagementItem>("VALIDATION_ERROR", "assignee is required (a user id, or \"me\").");
        }

        if (s.Status == "closed")
        {
            return EngagementResult.Fail<EngagementItem>("CONFLICT", "A closed enquiry cannot be reassigned.");
        }

        s.AssignedTo = who;
        if (s.Status == "new")
        {
            s.Status = "in_progress";
        }

        Log(id, "assigned", actor, who);
        await db.SaveChangesAsync();
        return EngagementResult.Ok(ToItem(s, await Targets(), time.GetUtcNow()));
    }

    /// <summary>Records that someone answered the enquiry (outside this system). The first one stops the SLA clock.</summary>
    public async Task<EngagementResult<EngagementItem>> RespondAsync(string id, string? note, string actor)
    {
        var s = await db.Submissions.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null)
        {
            return EngagementResult.Fail<EngagementItem>("NOT_FOUND", "Enquiry not found.");
        }

        if (s.Status == "closed")
        {
            return EngagementResult.Fail<EngagementItem>("CONFLICT", "The enquiry is already closed.");
        }

        var now = time.GetUtcNow();
        s.FirstResponseAt ??= now;
        s.Status = "responded";
        s.AssignedTo ??= actor;
        Log(id, "responded", actor, note?.Trim());
        await db.SaveChangesAsync();
        return EngagementResult.Ok(ToItem(s, await Targets(), now));
    }

    public async Task<EngagementResult<EngagementItem>> CloseAsync(string id, string? note, string actor)
    {
        var s = await db.Submissions.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null)
        {
            return EngagementResult.Fail<EngagementItem>("NOT_FOUND", "Enquiry not found.");
        }

        if (s.Status != "closed")
        {
            s.Status = "closed";
            s.ClosedAt = time.GetUtcNow();
            Log(id, "closed", actor, note?.Trim());
            await db.SaveChangesAsync();
        }

        return EngagementResult.Ok(ToItem(s, await Targets(), time.GetUtcNow()));
    }

    // ---- rules and targets

    public async Task<List<EngagementRuleView>> RulesAsync() =>
        [.. (await db.Set<EngagementRuleEntity>().AsNoTracking().ToListAsync()).OrderBy(r => r.Position).Select(RuleView)];

    /// <summary>Replaces the whole ordered rule list in one save, so the order the caller sent is the order that is applied.</summary>
    public async Task<EngagementResult<List<EngagementRuleView>>> SetRulesAsync(List<EngagementRuleWrite> rules)
    {
        if (rules.Count > 100)
        {
            return EngagementResult.Fail<List<EngagementRuleView>>("VALIDATION_ERROR", "At most 100 rules.");
        }

        var entities = new List<EngagementRuleEntity>();
        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            var error = ValidateRule(r);
            if (error is not null)
            {
                return EngagementResult.Fail<List<EngagementRuleView>>("VALIDATION_ERROR", $"Rule {i + 1}: {error}");
            }

            entities.Add(new EngagementRuleEntity
            {
                Id = $"rule_{Guid.NewGuid():N}",
                Position = i,
                Name = r.Name!.Trim(),
                MatchCategory = Blank(r.MatchCategory),
                MatchLanguage = Blank(r.MatchLanguage)?.ToLowerInvariant(),
                MatchKeyword = Blank(r.MatchKeyword),
                AssignTo = Blank(r.AssignTo),
                Priority = Blank(r.Priority),
                SetCategory = Blank(r.SetCategory),
                Active = r.Active ?? true
            });
        }

        db.Set<EngagementRuleEntity>().RemoveRange(await db.Set<EngagementRuleEntity>().ToListAsync());
        db.Set<EngagementRuleEntity>().AddRange(entities);
        await db.SaveChangesAsync();
        return EngagementResult.Ok(entities.Select(RuleView).ToList());
    }

    public async Task<Dictionary<string, int>> TargetsAsync() => await Targets();

    /// <summary>Sets targets per category in minutes; a null value removes that category's target (no clock).</summary>
    public async Task<EngagementResult<Dictionary<string, int>>> SetTargetsAsync(Dictionary<string, int?> targets)
    {
        foreach (var (category, minutes) in targets)
        {
            if (!EngagementCategories.All.Contains(category))
            {
                return EngagementResult.Fail<Dictionary<string, int>>("VALIDATION_ERROR", $"Unknown category '{category}'. Use: {string.Join(", ", EngagementCategories.All)}.");
            }

            if (minutes is < 1 or > 60 * 24 * 90)
            {
                return EngagementResult.Fail<Dictionary<string, int>>("VALIDATION_ERROR", $"{category}: minutes must be between 1 and {60 * 24 * 90}, or null to remove the target.");
            }
        }

        foreach (var (category, minutes) in targets)
        {
            var existing = await db.Set<EngagementSlaTargetEntity>().FirstOrDefaultAsync(t => t.Category == category);
            if (minutes is null)
            {
                if (existing is not null)
                {
                    db.Set<EngagementSlaTargetEntity>().Remove(existing);
                }
            }
            else if (existing is null)
            {
                db.Set<EngagementSlaTargetEntity>().Add(new EngagementSlaTargetEntity { Category = category, ResponseMinutes = minutes.Value });
            }
            else
            {
                existing.ResponseMinutes = minutes.Value;
            }
        }

        await db.SaveChangesAsync();
        return EngagementResult.Ok(await Targets());
    }

    // ---- reporting

    /// <summary>Per-category figures for enquiries received in the window, from the current targets.</summary>
    public async Task<EngagementReport> ReportAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var targets = await Targets();
        var now = time.GetUtcNow();
        var rows = (await db.Submissions.AsNoTracking().ToListAsync()).Where(s => s.CreatedAt >= from && s.CreatedAt < to).ToList();
        var categories = new List<EngagementCategoryReport>();
        foreach (var category in EngagementCategories.All)
        {
            var group = rows.Where(r => r.Category == category).ToList();
            targets.TryGetValue(category, out var target);
            var items = group.Select(g => ToItem(g, targets, now)).ToList();
            var minutes = group.Where(g => g.FirstResponseAt is not null).Select(g => (g.FirstResponseAt!.Value - g.CreatedAt).TotalMinutes).Order().ToList();
            categories.Add(new EngagementCategoryReport(
                category, group.Count, minutes.Count, group.Count(g => g.Status == "closed" && g.FirstResponseAt is null),
                group.Count(g => g.Status is "new" or "in_progress"),
                items.Count(i => i.SlaState == "breached"), items.Count(i => i.SlaState == "at_risk"), items.Count(i => i.SlaState == "met_late"),
                minutes.Count == 0 ? null : Math.Round(minutes.Average(), 1), minutes.Count == 0 ? null : Math.Round(Median(minutes), 1), target == 0 ? null : target));
        }

        return new EngagementReport(from, to, categories);
    }

    // ---- helpers

    private async Task<Dictionary<string, int>> Targets() =>
        (await db.Set<EngagementSlaTargetEntity>().AsNoTracking().ToListAsync()).ToDictionary(t => t.Category, t => t.ResponseMinutes);

    private void Log(string id, string type, string actor, string? detail) =>
        db.Set<EngagementEventEntity>().Add(new EngagementEventEntity { SubmissionId = id, Type = type, Actor = actor, Detail = detail, At = time.GetUtcNow() });

    private static EngagementItem ToItem(CtaSubmissionEntity s, Dictionary<string, int> targets, DateTimeOffset now)
    {
        // The clock starts when this service received the enquiry: the client-supplied submittedAt can be wrong or forged.
        var received = s.CreatedAt;
        int? target = targets.TryGetValue(s.Category, out var minutes) ? minutes : null;
        DateTimeOffset? due = target is null ? null : received.AddMinutes(target.Value);
        string state;
        int? toDue = null;
        if (target is null)
        {
            state = "none";
        }
        else if (s.FirstResponseAt is { } answered)
        {
            state = answered <= due ? "met" : "met_late";
        }
        else if (s.Status == "closed")
        {
            state = "closed_unanswered";
        }
        else
        {
            toDue = (int)Math.Floor((due!.Value - now).TotalMinutes);
            state = now > due ? "breached" : (due.Value - now).TotalMinutes <= target.Value * AtRiskFraction ? "at_risk" : "on_track";
        }

        return new EngagementItem(s.Id, s.Category, s.Status, s.Priority, s.AssignedTo, s.FormName, s.Language, s.SourcePath, received, s.FirstResponseAt, s.ClosedAt, target, due, state, toDue);
    }

    private static string? ValidateRule(EngagementRuleWrite r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 120)
        {
            return "name is required (max 120 characters).";
        }

        if (Blank(r.MatchCategory) is { } mc && !EngagementCategories.All.Contains(mc))
        {
            return $"matchCategory must be one of: {string.Join(", ", EngagementCategories.All)}.";
        }

        if (Blank(r.SetCategory) is { } sc && !EngagementCategories.All.Contains(sc))
        {
            return $"setCategory must be one of: {string.Join(", ", EngagementCategories.All)}.";
        }

        if (Blank(r.Priority) is { } p && !EngagementPriorities.All.Contains(p))
        {
            return $"priority must be one of: {string.Join(", ", EngagementPriorities.All)}.";
        }

        if (Blank(r.MatchLanguage) is { } l && l.ToLowerInvariant() is not ("bn" or "en"))
        {
            return "matchLanguage must be bn or en.";
        }

        if (Blank(r.MatchCategory) is null && Blank(r.MatchLanguage) is null && Blank(r.MatchKeyword) is null)
        {
            return "a rule needs at least one of matchCategory, matchLanguage or matchKeyword (a rule that matches everything would swallow every enquiry).";
        }

        if (Blank(r.AssignTo) is null && Blank(r.Priority) is null && Blank(r.SetCategory) is null)
        {
            return "a rule must assign, set a priority or set a category.";
        }

        return Blank(r.MatchKeyword) is { Length: > 120 } ? "matchKeyword is at most 120 characters." : null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double Median(List<double> sorted) => sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

    private static EngagementRuleView RuleView(EngagementRuleEntity r) =>
        new(r.Id, r.Position, r.Name, r.MatchCategory, r.MatchLanguage, r.MatchKeyword, r.AssignTo, r.Priority, r.SetCategory, r.Active);
}
