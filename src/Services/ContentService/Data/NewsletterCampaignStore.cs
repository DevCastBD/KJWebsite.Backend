using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentService.Data;

public sealed record NewsletterCampaignVersions(string? SubjectBn, string? BodyBn, string? SubjectEn, string? BodyEn);
public sealed record NewsletterTemplateRequest(string? Name, NewsletterCampaignVersions? Versions);
public sealed record NewsletterTemplateView(string Id, string Name, NewsletterCampaignVersions Versions, DateTimeOffset CreatedAt);
public sealed record NewsletterCampaignRequest(string? Name, string? TemplateId, string? SegmentId, NewsletterCampaignVersions? Versions);
public sealed record NewsletterCampaignView(string Id, string Name, string? SegmentId, NewsletterCampaignVersions Versions, string Status, int RecipientCount, DateTimeOffset? SendStartsAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record NewsletterTestSendRequest(string? Email, string? Lang);
public sealed record NewsletterSendRequest(int? ConfirmRecipientCount, DateTimeOffset? ScheduledAt);
public sealed record NewsletterCampaignReport(string CampaignId, string Status, int Recipients, int Queued, int Sent, int Failed, int Skipped, int Cancelled, int Bounced, int Complaints, int Unsubscribed, Dictionary<string, int> ByLanguage, bool Complete);

public sealed record CampaignResult<T>(T? Value, string? Code, string? Error, int? Actual = null);

public static class CampaignResult
{
    public static CampaignResult<T> Ok<T>(T value) => new(value, null, null);
    public static CampaignResult<T> Fail<T>(string code, string error, int? actual = null) => new(default, code, error, actual);
}

/// <summary>
/// Campaigns: bilingual drafts, previews, test sends and the irreversible send. A send needs both language versions (checked here, not only in the composer)
/// and an explicit confirmation carrying the exact recipient count. The audience is snapshotted into the outbox with each recipient's own language,
/// spaced at the configured rate, and every message re-checks consent at delivery (see <see cref="NewsletterDispatcher"/>).
/// </summary>
public sealed class NewsletterCampaignStore(ContentDbContext db, NewsletterStore newsletter, TimeProvider time)
{
    public const string Draft = "draft";
    public const string Sending = "sending";
    public const string Cancelled = "cancelled";

    // ---- templates

    public async Task<CampaignResult<NewsletterTemplateView>> CreateTemplateAsync(NewsletterTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
        {
            return CampaignResult.Fail<NewsletterTemplateView>("VALIDATION_ERROR", "name is required (max 120 characters).");
        }

        var versions = request.Versions;
        var problem = Complete(versions);
        if (problem is not null)
        {
            return CampaignResult.Fail<NewsletterTemplateView>("VALIDATION_ERROR", problem);
        }

        var entity = new NewsletterTemplateEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name.Trim(),
            SubjectBn = versions!.SubjectBn!.Trim(),
            BodyBn = versions.BodyBn!,
            SubjectEn = versions.SubjectEn!.Trim(),
            BodyEn = versions.BodyEn!,
            CreatedAt = time.GetUtcNow()
        };
        db.NewsletterTemplates.Add(entity);
        await db.SaveChangesAsync();
        return CampaignResult.Ok(new NewsletterTemplateView(entity.Id, entity.Name, new NewsletterCampaignVersions(entity.SubjectBn, entity.BodyBn, entity.SubjectEn, entity.BodyEn), entity.CreatedAt));
    }

    public async Task<List<NewsletterTemplateView>> ListTemplatesAsync() =>
        [.. (await db.NewsletterTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync()).Select(t => new NewsletterTemplateView(t.Id, t.Name, new NewsletterCampaignVersions(t.SubjectBn, t.BodyBn, t.SubjectEn, t.BodyEn), t.CreatedAt))];

    // ---- campaigns

    /// <summary>Creates a draft. Versions may be partial while drafting (they are only required complete to send), from a template and/or the request.</summary>
    public async Task<CampaignResult<NewsletterCampaignView>> CreateAsync(NewsletterCampaignRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("VALIDATION_ERROR", "name is required (max 120 characters).");
        }

        var entity = new NewsletterCampaignEntity { Id = Guid.NewGuid().ToString("N"), Name = request.Name.Trim(), CreatedAt = time.GetUtcNow() };
        if (request.TemplateId is not null)
        {
            var template = await db.NewsletterTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TemplateId);
            if (template is null)
            {
                return CampaignResult.Fail<NewsletterCampaignView>("VALIDATION_ERROR", "templateId does not exist.");
            }

            entity.SubjectBn = template.SubjectBn;
            entity.BodyBn = template.BodyBn;
            entity.SubjectEn = template.SubjectEn;
            entity.BodyEn = template.BodyEn;
        }

        var failure = await Apply(entity, request.SegmentId, request.Versions);
        if (failure is not null)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("VALIDATION_ERROR", failure);
        }

        entity.UpdatedAt = entity.CreatedAt;
        db.NewsletterCampaigns.Add(entity);
        await db.SaveChangesAsync();
        return CampaignResult.Ok(View(entity));
    }

    public async Task<CampaignResult<NewsletterCampaignView>> UpdateAsync(string id, NewsletterCampaignRequest request)
    {
        var entity = await db.NewsletterCampaigns.FirstOrDefaultAsync(c => c.Id == id);
        if (entity is null)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("NOT_FOUND", "Campaign not found.");
        }

        if (entity.Status != Draft)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("CONFLICT", "Only a draft can be edited; this campaign has been sent or cancelled.");
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            entity.Name = request.Name.Trim();
        }

        var failure = await Apply(entity, request.SegmentId, request.Versions);
        if (failure is not null)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("VALIDATION_ERROR", failure);
        }

        entity.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        return CampaignResult.Ok(View(entity));
    }

    public async Task<NewsletterCampaignView?> GetAsync(string id)
    {
        var entity = await db.NewsletterCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        return entity is null ? null : View(entity);
    }

    public async Task<List<NewsletterCampaignView>> ListAsync() =>
        [.. (await db.NewsletterCampaigns.AsNoTracking().ToListAsync()).OrderByDescending(c => c.CreatedAt).Select(View)];

    /// <summary>The exact message a subscriber in `lang` would get, with sample links. Fails (a 400 to the composer) if the version uses an unknown variable.</summary>
    public async Task<CampaignResult<RenderedEmail>> PreviewAsync(string id, string? lang)
    {
        var campaign = await db.NewsletterCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (campaign is null)
        {
            return CampaignResult.Fail<RenderedEmail>("NOT_FOUND", "Campaign not found.");
        }

        var chosen = lang is null ? "bn" : lang.Trim().ToLowerInvariant();
        if (chosen is not ("bn" or "en"))
        {
            return CampaignResult.Fail<RenderedEmail>("VALIDATION_ERROR", "lang must be bn or en.");
        }

        return Try(() => RenderFor(campaign, chosen, "preview@example.org", "https://example.org/unsubscribe", "https://example.org/preferences"));
    }

    /// <summary>Queues a test message to any address now, rendered by the same code as the real send. It carries no recipient-specific unsubscribe link.</summary>
    public async Task<CampaignResult<RenderedEmail>> TestSendAsync(string id, NewsletterTestSendRequest request)
    {
        var campaign = await db.NewsletterCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (campaign is null)
        {
            return CampaignResult.Fail<RenderedEmail>("NOT_FOUND", "Campaign not found.");
        }

        var email = request.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out _))
        {
            return CampaignResult.Fail<RenderedEmail>("VALIDATION_ERROR", "Enter a valid email address.");
        }

        var lang = request.Lang is null ? "bn" : request.Lang.Trim().ToLowerInvariant();
        if (lang is not ("bn" or "en"))
        {
            return CampaignResult.Fail<RenderedEmail>("VALIDATION_ERROR", "lang must be bn or en.");
        }

        var rendered = Try(() => RenderFor(campaign, lang, email, "https://example.org/unsubscribe-test", "https://example.org/preferences-test"));
        if (rendered.Value is null)
        {
            return rendered;
        }

        db.NewsletterMessages.Add(new NewsletterMessageEntity
        {
            SubscriberId = "test",
            ToEmail = email,
            Kind = "test",
            Lang = lang,
            Subject = "[TEST] " + rendered.Value.Subject,
            Body = rendered.Value.Text,
            BodyHtml = rendered.Value.Html,
            CampaignId = campaign.Id,
            CreatedAt = time.GetUtcNow()
        });
        await db.SaveChangesAsync();
        return rendered;
    }

    /// <summary>The send. Irreversible, so: draft only, both languages complete and renderable, and `confirmRecipientCount` must equal the audience right now.</summary>
    public async Task<CampaignResult<NewsletterCampaignView>> SendAsync(string id, NewsletterSendRequest request)
    {
        var campaign = await db.NewsletterCampaigns.FirstOrDefaultAsync(c => c.Id == id);
        if (campaign is null)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("NOT_FOUND", "Campaign not found.");
        }

        if (campaign.Status != Draft)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("CONFLICT", "This campaign has already been sent or cancelled.");
        }

        var versions = new NewsletterCampaignVersions(campaign.SubjectBn, campaign.BodyBn, campaign.SubjectEn, campaign.BodyEn);
        var problem = Complete(versions);
        if (problem is not null)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("MISSING_LANGUAGE", problem);
        }

        var now = time.GetUtcNow();
        if (request.ScheduledAt is { } at && at < now.AddMinutes(-1))
        {
            return CampaignResult.Fail<NewsletterCampaignView>("VALIDATION_ERROR", "scheduledAt cannot be in the past.");
        }

        var audience = await newsletter.AudienceAsync(campaign.SegmentId);
        if (audience is null)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("VALIDATION_ERROR", "The campaign's segment no longer exists.");
        }

        if (audience.Count == 0)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("VALIDATION_ERROR", "The audience is empty: nobody has confirmed.");
        }

        if (request.ConfirmRecipientCount != audience.Count)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("RECIPIENT_COUNT_MISMATCH", $"confirmRecipientCount must equal the current audience size ({audience.Count}).", audience.Count);
        }

        var start = (request.ScheduledAt ?? now).ToUnixTimeSeconds();
        var perMinute = newsletter.SendPerMinute;
        var messages = new List<NewsletterMessageEntity>(audience.Count);
        try
        {
            for (var i = 0; i < audience.Count; i++)
            {
                var subscriber = audience[i];
                var (unsubscribe, preferences) = newsletter.Links(subscriber.Id);
                var rendered = RenderFor(campaign, subscriber.Lang, subscriber.Email, unsubscribe, preferences);
                messages.Add(new NewsletterMessageEntity
                {
                    SubscriberId = subscriber.Id,
                    ToEmail = subscriber.Email,
                    Kind = "campaign",
                    Lang = subscriber.Lang,
                    Subject = rendered.Subject,
                    Body = rendered.Text,
                    BodyHtml = rendered.Html,
                    ListUnsubscribeUrl = unsubscribe,
                    CampaignId = campaign.Id,
                    NotBeforeUnix = start + (long)i * 60 / perMinute,
                    CreatedAt = now
                });
            }
        }
        catch (RenderException ex)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("RENDER_ERROR", ex.Message);
        }

        db.NewsletterMessages.AddRange(messages);
        campaign.Status = Sending;
        campaign.RecipientCount = audience.Count;
        campaign.SendStartsAt = request.ScheduledAt ?? now;
        campaign.UpdatedAt = now;
        await db.SaveChangesAsync();
        return CampaignResult.Ok(View(campaign));
    }

    /// <summary>Stops a campaign: everything not yet delivered is cancelled. Already-delivered messages cannot be recalled.</summary>
    public async Task<CampaignResult<NewsletterCampaignView>> CancelAsync(string id)
    {
        var campaign = await db.NewsletterCampaigns.FirstOrDefaultAsync(c => c.Id == id);
        if (campaign is null)
        {
            return CampaignResult.Fail<NewsletterCampaignView>("NOT_FOUND", "Campaign not found.");
        }

        if (campaign.Status == Cancelled)
        {
            return CampaignResult.Ok(View(campaign));
        }

        foreach (var message in await db.NewsletterMessages.Where(m => m.CampaignId == id && m.Status == "queued").ToListAsync())
        {
            message.Status = "cancelled";
        }

        campaign.Status = Cancelled;
        campaign.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        return CampaignResult.Ok(View(campaign));
    }

    /// <summary>Delivery numbers from the outbox, plus what happened to the recipients afterwards (suppressed by bounce or complaint, unsubscribed since the send began).</summary>
    public async Task<NewsletterCampaignReport?> ReportAsync(string id)
    {
        var campaign = await db.NewsletterCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (campaign is null)
        {
            return null;
        }

        var messages = await db.NewsletterMessages.AsNoTracking().Where(m => m.CampaignId == id && m.Kind == "campaign").Select(m => new { m.SubscriberId, m.Status, m.Lang }).ToListAsync();
        var recipientIds = messages.Select(m => m.SubscriberId).ToHashSet();
        var subscribers = recipientIds.Count == 0 ? [] : await db.NewsletterSubscribers.AsNoTracking().Where(s => recipientIds.Contains(s.Id)).ToListAsync();
        var begun = campaign.SendStartsAt;
        int Count(string status) => messages.Count(m => m.Status == status);
        var queued = Count("queued");
        return new NewsletterCampaignReport(
            id, campaign.Status, messages.Count, queued, Count("sent"), Count("failed"), Count("skipped"), Count("cancelled"),
            subscribers.Count(s => s.Status == SubscriberStatuses.Suppressed && s.SuppressionReason == "bounce"),
            subscribers.Count(s => s.Status == SubscriberStatuses.Suppressed && s.SuppressionReason == "complaint"),
            subscribers.Count(s => s.Status == SubscriberStatuses.Unsubscribed && begun is not null && s.UnsubscribedAt >= begun),
            messages.GroupBy(m => m.Lang).ToDictionary(g => g.Key, g => g.Count()),
            campaign.Status != Sending || queued == 0);
    }

    // ---- helpers

    private async Task<string?> Apply(NewsletterCampaignEntity entity, string? segmentId, NewsletterCampaignVersions? versions)
    {
        if (segmentId is not null)
        {
            if (!await db.NewsletterSegments.AnyAsync(s => s.Id == segmentId))
            {
                return "segmentId does not exist.";
            }

            entity.SegmentId = segmentId;
        }

        if (versions is null)
        {
            return null;
        }

        entity.SubjectBn = versions.SubjectBn ?? entity.SubjectBn;
        entity.BodyBn = versions.BodyBn ?? entity.BodyBn;
        entity.SubjectEn = versions.SubjectEn ?? entity.SubjectEn;
        entity.BodyEn = versions.BodyEn ?? entity.BodyEn;
        foreach (var (lang, subject, body) in new[] { ("bn", entity.SubjectBn, entity.BodyBn), ("en", entity.SubjectEn, entity.BodyEn) })
        {
            if (subject.Length > 300)
            {
                return $"The {lang} subject is longer than 300 characters.";
            }

            try
            {
                NewsletterRenderer.Validate(lang, subject, body);
            }
            catch (RenderException ex)
            {
                return $"{lang}: {ex.Message}";
            }
        }

        return null;
    }

    /// <summary>The server-side rule behind "a campaign cannot send with a missing language version".</summary>
    private static string? Complete(NewsletterCampaignVersions? v)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(v?.SubjectBn))
        {
            missing.Add("subjectBn");
        }

        if (string.IsNullOrWhiteSpace(v?.BodyBn))
        {
            missing.Add("bodyBn");
        }

        if (string.IsNullOrWhiteSpace(v?.SubjectEn))
        {
            missing.Add("subjectEn");
        }

        if (string.IsNullOrWhiteSpace(v?.BodyEn))
        {
            missing.Add("bodyEn");
        }

        return missing.Count == 0 ? null : $"Both language versions are required; missing: {string.Join(", ", missing)}.";
    }

    private static RenderedEmail RenderFor(NewsletterCampaignEntity campaign, string lang, string email, string unsubscribeUrl, string preferencesUrl)
    {
        var (subject, body) = lang == "bn" ? (campaign.SubjectBn, campaign.BodyBn) : (campaign.SubjectEn, campaign.BodyEn);
        return NewsletterRenderer.Render(lang, subject, body, new Dictionary<string, string> { ["email"] = email, ["unsubscribeUrl"] = unsubscribeUrl, ["preferencesUrl"] = preferencesUrl });
    }

    private static CampaignResult<RenderedEmail> Try(Func<RenderedEmail> render)
    {
        try
        {
            return CampaignResult.Ok(render());
        }
        catch (RenderException ex)
        {
            return CampaignResult.Fail<RenderedEmail>("RENDER_ERROR", ex.Message);
        }
    }

    private static NewsletterCampaignView View(NewsletterCampaignEntity c) =>
        new(c.Id, c.Name, c.SegmentId, new NewsletterCampaignVersions(c.SubjectBn, c.BodyBn, c.SubjectEn, c.BodyEn), c.Status, c.RecipientCount, c.SendStartsAt, c.CreatedAt, c.UpdatedAt);
}

/// <summary>The mail transport. The default only logs; an SMTP or provider implementation replaces it without touching the queue.</summary>
public interface INewsletterTransport
{
    Task SendAsync(NewsletterMessageEntity message, CancellationToken ct);
}

public sealed partial class LoggingNewsletterTransport(ILogger<LoggingNewsletterTransport> logger) : INewsletterTransport
{
    public Task SendAsync(NewsletterMessageEntity message, CancellationToken ct)
    {
        Log(logger, message.Kind, message.Id);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Newsletter message ({Kind} #{Id}) handed to the logging transport; no mail is sent until a real transport is configured.")]
    private static partial void Log(ILogger logger, string kind, long id);
}

/// <summary>
/// Delivers due messages. A campaign message is re-checked at delivery: if the person unsubscribed, bounced or complained since the send was queued,
/// it is skipped, so consent is honoured up to the last moment.
/// </summary>
public sealed partial class NewsletterDispatcher(IServiceScopeFactory scopes, TimeProvider time, Microsoft.Extensions.Options.IOptions<NewsletterOptions> options, ILogger<NewsletterDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DispatchEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        do
        {
            try
            {
                await DispatchDueAsync(100, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                CycleFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over up to `batch` due messages. Returns how many were handed to the transport. Public so tests can drive it.</summary>
    public async Task<int> DispatchDueAsync(int batch, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
        var transport = scope.ServiceProvider.GetRequiredService<INewsletterTransport>();
        var now = time.GetUtcNow();
        var due = await db.NewsletterMessages.Where(m => m.Status == "queued" && m.NotBeforeUnix <= now.ToUnixTimeSeconds()).OrderBy(m => m.NotBeforeUnix).ThenBy(m => m.Id).Take(batch).ToListAsync(ct);
        var sent = 0;
        foreach (var message in due)
        {
            if (message.Kind == "campaign" && await db.NewsletterSubscribers.AsNoTracking().AnyAsync(s => s.Id == message.SubscriberId && s.Status == SubscriberStatuses.Confirmed, ct) is false)
            {
                message.Status = "skipped";
                continue;
            }

            try
            {
                await transport.SendAsync(message, ct);
                message.Status = "sent";
                message.SentAt = now;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Status = "failed";
                message.Error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            }
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Newsletter dispatch cycle failed; retrying at the next poll.")]
    private static partial void CycleFailed(ILogger logger, Exception exception);
}
