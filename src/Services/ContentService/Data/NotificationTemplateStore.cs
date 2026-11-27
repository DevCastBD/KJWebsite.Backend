using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public sealed record NotificationTemplateWrite(string? Subject, string? Body);
public sealed record NotificationRenderRequest(string? Lang, Dictionary<string, string>? Values, int? Version, string? ToEmail, bool? Record);
public sealed record NotificationTemplateView(string Key, string Lang, int Version, string Subject, string Body, string[] Variables, DateTimeOffset CreatedAt, string CreatedBy);
public sealed record NotificationRenderView(string Key, string Lang, int Version, string Subject, string Text, string Html, long? SendId);
public sealed record NotificationSendView(long Id, string Key, string Lang, int Version, string ToEmail, string Subject, string Body, DateTimeOffset CreatedAt);

public sealed record NotificationResult<T>(T? Value, string? Code, string? Error);

public static class NotificationResult
{
    public static NotificationResult<T> Ok<T>(T value) => new(value, null, null);
    public static NotificationResult<T> Fail<T>(string code, string error) => new(default, code, error);
}

/// <summary>
/// Versioned bilingual transactional templates. A version is never changed: an edit adds the next version, the newest is the one sent, and every recorded
/// send keeps the exact subject and body it produced plus the version number, so "what exactly did we tell them?" has an answer.
/// A template may only use the variables its catalogue entry declares, and a render needs every variable it uses, so mistakes fail at test time.
/// </summary>
public sealed class NotificationTemplateStore(ContentDbContext db, TimeProvider time)
{
    private static readonly string[] Languages = ["bn", "en"];

    /// <summary>Adds version 1 of every catalogue template that has none. Idempotent; never touches an edited template.</summary>
    public static async Task SeedAsync(ContentDbContext db, TimeProvider time)
    {
        var existing = (await db.NotificationTemplates.AsNoTracking().Select(t => new { t.Key, t.Lang }).Distinct().ToListAsync()).Select(t => (t.Key, t.Lang)).ToHashSet();
        foreach (var ((key, lang), (subject, body)) in NotificationCatalogue.Seed)
        {
            if (!existing.Contains((key, lang)))
            {
                db.NotificationTemplates.Add(new NotificationTemplateEntity { Id = Guid.NewGuid().ToString("N"), Key = key, Lang = lang, Version = 1, Subject = subject, Body = body, CreatedBy = "system", CreatedAt = time.GetUtcNow() });
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task<List<NotificationTemplateView>> LatestAsync()
    {
        var all = await db.NotificationTemplates.AsNoTracking().ToListAsync();
        return [.. all.GroupBy(t => (t.Key, t.Lang)).Select(g => g.OrderByDescending(t => t.Version).First()).OrderBy(t => t.Key, StringComparer.Ordinal).ThenBy(t => t.Lang, StringComparer.Ordinal).Select(View)];
    }

    public async Task<List<NotificationTemplateView>?> VersionsAsync(string key, string lang)
    {
        if (Spec(key) is null)
        {
            return null;
        }

        return [.. (await db.NotificationTemplates.AsNoTracking().Where(t => t.Key == key && t.Lang == lang).ToListAsync()).OrderByDescending(t => t.Version).Select(View)];
    }

    /// <summary>Adds the next version. The text may use only the variables the catalogue declares for the key.</summary>
    public async Task<NotificationResult<NotificationTemplateView>> AddVersionAsync(string key, string lang, NotificationTemplateWrite write, string author)
    {
        var spec = Spec(key);
        if (spec is null)
        {
            return NotificationResult.Fail<NotificationTemplateView>("NOT_FOUND", "Unknown template key.");
        }

        if (!Languages.Contains(lang))
        {
            return NotificationResult.Fail<NotificationTemplateView>("VALIDATION_ERROR", "lang must be bn or en.");
        }

        if (string.IsNullOrWhiteSpace(write.Subject) || write.Subject.Length > 300 || string.IsNullOrWhiteSpace(write.Body))
        {
            return NotificationResult.Fail<NotificationTemplateView>("VALIDATION_ERROR", "subject (max 300 characters) and body are required.");
        }

        var unknown = NewsletterRenderer.UsedVariables(write.Subject).Concat(NewsletterRenderer.UsedVariables(write.Body)).Except(spec.Variables).Distinct().ToArray();
        if (unknown.Length > 0)
        {
            return NotificationResult.Fail<NotificationTemplateView>("VALIDATION_ERROR", $"Unknown variable(s) for {key}: {string.Join(", ", unknown)}. Allowed: {string.Join(", ", spec.Variables)}.");
        }

        var latest = await db.NotificationTemplates.Where(t => t.Key == key && t.Lang == lang).Select(t => (int?)t.Version).MaxAsync() ?? 0;
        var entity = new NotificationTemplateEntity { Id = Guid.NewGuid().ToString("N"), Key = key, Lang = lang, Version = latest + 1, Subject = write.Subject.Trim(), Body = write.Body, CreatedBy = author, CreatedAt = time.GetUtcNow() };
        db.NotificationTemplates.Add(entity);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return NotificationResult.Fail<NotificationTemplateView>("CONFLICT", "Someone saved a new version at the same moment. Reload and try again.");
        }

        return NotificationResult.Ok(View(entity));
    }

    /// <summary>
    /// Renders a template (the newest version unless `version` is given) for one recipient. Every declared variable the text uses must be supplied, or the render
    /// fails. With `record`, the result is stored with its version as the send record.
    /// </summary>
    public async Task<NotificationResult<NotificationRenderView>> RenderAsync(string key, NotificationRenderRequest request)
    {
        if (Spec(key) is null)
        {
            return NotificationResult.Fail<NotificationRenderView>("NOT_FOUND", "Unknown template key.");
        }

        var lang = string.IsNullOrWhiteSpace(request.Lang) ? "bn" : request.Lang.Trim().ToLowerInvariant();
        if (!Languages.Contains(lang))
        {
            return NotificationResult.Fail<NotificationRenderView>("VALIDATION_ERROR", "lang must be bn or en.");
        }

        var versions = db.NotificationTemplates.AsNoTracking().Where(t => t.Key == key && t.Lang == lang);
        var template = request.Version is { } v
            ? await versions.FirstOrDefaultAsync(t => t.Version == v)
            : (await versions.ToListAsync()).OrderByDescending(t => t.Version).FirstOrDefault();
        if (template is null)
        {
            return NotificationResult.Fail<NotificationRenderView>("NOT_FOUND", "That template version does not exist.");
        }

        RenderedEmail rendered;
        try
        {
            rendered = NewsletterRenderer.RenderMessage(lang, template.Subject, template.Body, request.Values ?? [], string.Empty, string.Empty);
        }
        catch (RenderException ex)
        {
            return NotificationResult.Fail<NotificationRenderView>("RENDER_ERROR", ex.Message);
        }

        long? sendId = null;
        if (request.Record == true)
        {
            var to = request.ToEmail?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(to) || to.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(to, out _))
            {
                return NotificationResult.Fail<NotificationRenderView>("VALIDATION_ERROR", "toEmail is required to record a send.");
            }

            var send = new NotificationSendEntity { Key = key, Lang = lang, Version = template.Version, ToEmail = to, Subject = rendered.Subject, Body = rendered.Text, BodyHtml = rendered.Html, CreatedAt = time.GetUtcNow() };
            db.NotificationSends.Add(send);
            await db.SaveChangesAsync();
            sendId = send.Id;
        }

        return NotificationResult.Ok(new NotificationRenderView(key, lang, template.Version, rendered.Subject, rendered.Text, rendered.Html, sendId));
    }

    public async Task<List<NotificationSendView>> SendsAsync(string? key, int limit, int offset)
    {
        var rows = db.NotificationSends.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(key))
        {
            rows = rows.Where(s => s.Key == key);
        }

        return [.. (await rows.OrderByDescending(s => s.Id).Skip(offset).Take(limit).ToListAsync()).Select(s => new NotificationSendView(s.Id, s.Key, s.Lang, s.Version, s.ToEmail, s.Subject, s.Body, s.CreatedAt))];
    }

    private static NotificationTemplateSpec? Spec(string key) => NotificationCatalogue.Specs.FirstOrDefault(s => s.Key == key);

    private static NotificationTemplateView View(NotificationTemplateEntity t) =>
        new(t.Key, t.Lang, t.Version, t.Subject, t.Body, Spec(t.Key)?.Variables ?? [], t.CreatedAt, t.CreatedBy);
}
