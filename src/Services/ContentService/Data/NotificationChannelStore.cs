using System.Text.RegularExpressions;
using ContentService.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContentService.Data;

public sealed class ChannelOptions
{
    public const string SectionName = "Channels";

    /// <summary>Most SMS one phone number may receive in 24 hours.</summary>
    public int SmsPerRecipientPerDay { get; set; } = 3;

    /// <summary>Most SMS the whole system may queue in 24 hours: a ceiling on the cost of any mistake.</summary>
    public int SmsDailyLimit { get; set; } = 1000;

    /// <summary>Longest SMS accepted, in billable segments.</summary>
    public int SmsMaxSegments { get; set; } = 3;

    public bool DispatchEnabled { get; set; } = true;
}

public sealed record NotificationPreferenceView(bool Email, bool Sms, bool InApp, bool Push, string? Phone, string Lang);
public sealed record NotificationPreferenceWrite(bool? Email, bool? Sms, bool? InApp, bool? Push, string? Phone, string? Lang);
public sealed record InAppView(long Id, string Key, string Title, string Body, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
public sealed record InboxView(List<InAppView> Items, int Unread, int Total);
public sealed record PushSubscriptionWrite(string? Endpoint, string? P256dh, string? Auth, bool? Consent);
public sealed record NotifyRequest(string? Subject, string? Email, string? Phone, string? Lang, Dictionary<string, string>? Values, bool? AllowSms);
public sealed record ChannelOutcome(string Channel, string Status, string? Reason);
public sealed record SmsView(long Id, string ToPhone, string Key, string Lang, string Encoding, int Segments, string Status, string Body, DateTimeOffset CreatedAt);
public sealed record SmsLength(string Encoding, int Segments);

/// <summary>
/// SMS, in-app and web-push channels. A person's channel choices are honoured on every notification; SMS has four independent gates because it costs money:
/// the sender must set `allowSms` for this notification, the template must be on the SMS allow-list, the person must have opted in with a valid Bangladeshi number,
/// and per-number and daily limits must hold.
/// </summary>
public sealed partial class NotificationChannelStore(ContentDbContext db, NotificationTemplateStore templates, IOptions<ChannelOptions> options, TimeProvider time)
{
    /// <summary>Templates that may be sent by SMS. Everything else is email or in-app only.</summary>
    public static readonly IReadOnlySet<string> SmsTemplates = new HashSet<string> { "event.reminder", "dues.reminder", "auth.password-reset" };

    private const string Gsm7Basic = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string Gsm7Extension = "^{}\\[~]|€\f";
    private readonly ChannelOptions _options = options.Value;

    // ---- preferences

    public async Task<NotificationPreferenceView> GetPreferencesAsync(string subject) =>
        View(await db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.Subject == subject) ?? new NotificationPreferenceEntity { Subject = subject });

    public async Task<(NotificationPreferenceView? View, string? Error)> SetPreferencesAsync(string subject, NotificationPreferenceWrite write)
    {
        var entity = await db.NotificationPreferences.FirstOrDefaultAsync(p => p.Subject == subject);
        var isNew = entity is null;
        entity ??= new NotificationPreferenceEntity { Subject = subject };
        if (write.Phone is not null)
        {
            if (write.Phone.Length == 0)
            {
                entity.Phone = null;
            }
            else if (NormalizePhone(write.Phone) is { } phone)
            {
                entity.Phone = phone;
            }
            else
            {
                return (null, "Enter a Bangladeshi mobile number, for example 01712345678 or +8801712345678.");
            }
        }

        if (write.Lang is not null)
        {
            var lang = write.Lang.Trim().ToLowerInvariant();
            if (lang is not ("bn" or "en"))
            {
                return (null, "lang must be bn or en.");
            }

            entity.Lang = lang;
        }

        entity.Email = write.Email ?? entity.Email;
        entity.InApp = write.InApp ?? entity.InApp;
        entity.Sms = write.Sms ?? entity.Sms;
        entity.Push = write.Push ?? entity.Push;
        if (entity.Sms && entity.Phone is null)
        {
            return (null, "Add a mobile number to receive SMS.");
        }

        entity.UpdatedAt = time.GetUtcNow();
        if (isNew)
        {
            db.NotificationPreferences.Add(entity);
        }

        if (!entity.Push)
        {
            db.PushSubscriptions.RemoveRange(db.PushSubscriptions.Where(s => s.Subject == subject));
        }

        await db.SaveChangesAsync();
        return (View(entity), null);
    }

    // ---- in-app inbox

    public async Task<InboxView> InboxAsync(string subject, bool unreadOnly, int limit, int offset)
    {
        var rows = db.InAppNotifications.AsNoTracking().Where(n => n.Subject == subject);
        var unread = await rows.CountAsync(n => n.ReadAt == null);
        var filtered = unreadOnly ? rows.Where(n => n.ReadAt == null) : rows;
        var total = await filtered.CountAsync();
        var page = await filtered.OrderByDescending(n => n.Id).Skip(offset).Take(limit).ToListAsync();
        return new InboxView([.. page.Select(n => new InAppView(n.Id, n.Key, n.Title, n.Body, n.CreatedAt, n.ReadAt))], unread, total);
    }

    public async Task<bool> MarkReadAsync(string subject, long id)
    {
        var item = await db.InAppNotifications.FirstOrDefaultAsync(n => n.Id == id && n.Subject == subject);
        if (item is null)
        {
            return false;
        }

        item.ReadAt ??= time.GetUtcNow();
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<int> MarkAllReadAsync(string subject)
    {
        var unread = await db.InAppNotifications.Where(n => n.Subject == subject && n.ReadAt == null).ToListAsync();
        var now = time.GetUtcNow();
        foreach (var item in unread)
        {
            item.ReadAt = now;
        }

        await db.SaveChangesAsync();
        return unread.Count;
    }

    // ---- web push

    /// <summary>Stores a push subscription. Needs `consent: true` (the member's explicit opt-in) and turns the push preference on.</summary>
    public async Task<string?> AddPushAsync(string subject, PushSubscriptionWrite write)
    {
        if (write.Consent != true)
        {
            return "Push notifications need explicit consent: send consent=true after the member agrees.";
        }

        if (string.IsNullOrWhiteSpace(write.Endpoint) || !Uri.TryCreate(write.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || write.Endpoint.Length > 1000
            || string.IsNullOrWhiteSpace(write.P256dh) || string.IsNullOrWhiteSpace(write.Auth) || write.P256dh.Length > 200 || write.Auth.Length > 100)
        {
            return "endpoint (https), p256dh and auth are required.";
        }

        if (!await db.PushSubscriptions.AnyAsync(s => s.Endpoint == write.Endpoint))
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity { Id = Guid.NewGuid().ToString("N"), Subject = subject, Endpoint = write.Endpoint, P256dh = write.P256dh, Auth = write.Auth, ConsentedAt = time.GetUtcNow() });
        }

        var preference = await db.NotificationPreferences.FirstOrDefaultAsync(p => p.Subject == subject);
        if (preference is null)
        {
            db.NotificationPreferences.Add(new NotificationPreferenceEntity { Subject = subject, Push = true, UpdatedAt = time.GetUtcNow() });
        }
        else
        {
            preference.Push = true;
            preference.UpdatedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync();
        return null;
    }

    public async Task<bool> RemovePushAsync(string subject, string endpoint)
    {
        var row = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Subject == subject && s.Endpoint == endpoint);
        if (row is null)
        {
            return false;
        }

        db.PushSubscriptions.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    // ---- notify

    /// <summary>
    /// Sends one catalogue message to one person over the channels they allow. Returns what happened per channel, with the reason for each one that was skipped.
    /// Email is reported as `queued` once a send record exists; delivery of email is the transport's job.
    /// </summary>
    public async Task<(List<ChannelOutcome>? Outcomes, string? Code, string? Error)> NotifyAsync(string key, NotifyRequest request)
    {
        var preference = string.IsNullOrWhiteSpace(request.Subject) ? null : await db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.Subject == request.Subject);
        var lang = string.IsNullOrWhiteSpace(request.Lang) ? preference?.Lang ?? "bn" : request.Lang;
        var rendered = await templates.RenderAsync(key, new NotificationRenderRequest(lang, request.Values, null, null, false));
        if (rendered.Value is null)
        {
            return (null, rendered.Code, rendered.Error);
        }

        var view = rendered.Value;
        var outcomes = new List<ChannelOutcome>();
        var now = time.GetUtcNow();
        var wantsEmail = preference?.Email ?? true;
        var wantsInApp = preference?.InApp ?? true;
        var wantsSms = preference?.Sms ?? false;

        // email
        if (!wantsEmail)
        {
            outcomes.Add(new ChannelOutcome("email", "skipped", "RECIPIENT_OPTED_OUT"));
        }
        else if (string.IsNullOrWhiteSpace(request.Email) || !System.Net.Mail.MailAddress.TryCreate(request.Email.Trim(), out _))
        {
            outcomes.Add(new ChannelOutcome("email", "skipped", "NO_ADDRESS"));
        }
        else
        {
            db.NotificationSends.Add(new NotificationSendEntity { Key = key, Lang = view.Lang, Version = view.Version, ToEmail = request.Email.Trim().ToLowerInvariant(), Subject = view.Subject, Body = view.Text, BodyHtml = view.Html, CreatedAt = now });
            outcomes.Add(new ChannelOutcome("email", "queued", null));
        }

        // in-app
        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            outcomes.Add(new ChannelOutcome("inApp", "skipped", "NO_MEMBER"));
        }
        else if (!wantsInApp)
        {
            outcomes.Add(new ChannelOutcome("inApp", "skipped", "RECIPIENT_OPTED_OUT"));
        }
        else
        {
            db.InAppNotifications.Add(new InAppNotificationEntity { Subject = request.Subject, Key = key, Title = view.Subject, Body = view.Text, CreatedAt = now });
            outcomes.Add(new ChannelOutcome("inApp", "queued", null));
        }

        // sms
        outcomes.Add(await QueueSmsAsync(key, view, request, preference, wantsSms, now));
        await db.SaveChangesAsync();
        return (outcomes, null, null);
    }

    private async Task<ChannelOutcome> QueueSmsAsync(string key, NotificationRenderView view, NotifyRequest request, NotificationPreferenceEntity? preference, bool wantsSms, DateTimeOffset now)
    {
        if (request.AllowSms != true)
        {
            return new ChannelOutcome("sms", "skipped", "SMS_NOT_ALLOWED_FOR_THIS_SEND");
        }

        if (!SmsTemplates.Contains(key))
        {
            return new ChannelOutcome("sms", "skipped", "TEMPLATE_NOT_APPROVED_FOR_SMS");
        }

        if (!wantsSms)
        {
            return new ChannelOutcome("sms", "skipped", "RECIPIENT_NOT_OPTED_IN");
        }

        var phone = NormalizePhone(request.Phone ?? preference?.Phone ?? string.Empty);
        if (phone is null)
        {
            return new ChannelOutcome("sms", "skipped", "NO_VALID_BANGLADESH_NUMBER");
        }

        var text = SmsText(view.Text);
        var length = Measure(text);
        if (length.Segments > _options.SmsMaxSegments)
        {
            return new ChannelOutcome("sms", "skipped", $"TOO_LONG ({length.Segments} segments, max {_options.SmsMaxSegments})");
        }

        var since = now.AddHours(-24).ToUnixTimeSeconds();
        var recent = db.SmsMessages.Where(s => s.CreatedUnix >= since && s.Status != "failed");
        if (await recent.CountAsync(s => s.ToPhone == phone) >= _options.SmsPerRecipientPerDay)
        {
            return new ChannelOutcome("sms", "skipped", "RECIPIENT_DAILY_LIMIT");
        }

        if (await recent.CountAsync() >= _options.SmsDailyLimit)
        {
            return new ChannelOutcome("sms", "skipped", "DAILY_LIMIT");
        }

        db.SmsMessages.Add(new SmsMessageEntity
        {
            Subject = request.Subject ?? string.Empty,
            ToPhone = phone,
            Key = key,
            Lang = view.Lang,
            Body = text,
            Encoding = length.Encoding,
            Segments = length.Segments,
            CreatedUnix = now.ToUnixTimeSeconds(),
            CreatedAt = now
        });
        return new ChannelOutcome("sms", "queued", null);
    }

    public async Task<List<SmsView>> SmsAsync(string? status, int limit, int offset)
    {
        var rows = db.SmsMessages.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            rows = rows.Where(s => s.Status == status);
        }

        return [.. (await rows.OrderByDescending(s => s.Id).Skip(offset).Take(limit).ToListAsync()).Select(s => new SmsView(s.Id, s.ToPhone, s.Key, s.Lang, s.Encoding, s.Segments, s.Status, s.Body, s.CreatedAt))];
    }

    // ---- SMS text and numbers

    /// <summary>`+8801XXXXXXXXX` from `01XXXXXXXXX`, `8801XXXXXXXXX` or `+8801XXXXXXXXX` (operator digit 3 to 9); null for anything else.</summary>
    public static string? NormalizePhone(string raw)
    {
        var compact = SeparatorChars().Replace(raw, string.Empty);
        var match = BdMobile().Match(compact);
        return match.Success ? "+880" + match.Groups[1].Value : null;
    }

    /// <summary>
    /// Billable segments. GSM-7 text is 160 characters (153 per part when split); any character outside it, such as every Bengali letter, makes the message UCS-2:
    /// 70 characters (67 per part). A Bengali message therefore costs about 2.3 times as many segments as the same length in English.
    /// </summary>
    public static SmsLength Measure(string text)
    {
        var units = 0;
        var gsm = true;
        foreach (var c in text)
        {
            if (Gsm7Basic.Contains(c, StringComparison.Ordinal))
            {
                units++;
            }
            else if (Gsm7Extension.Contains(c, StringComparison.Ordinal))
            {
                units += 2;
            }
            else
            {
                gsm = false;
                break;
            }
        }

        if (gsm)
        {
            return new SmsLength("gsm7", units <= 160 ? 1 : (int)Math.Ceiling(units / 153.0));
        }

        var ucs = text.Length; // UTF-16 code units, which is what UCS-2 counts
        return new SmsLength("ucs2", ucs <= 70 ? 1 : (int)Math.Ceiling(ucs / 67.0));
    }

    private static string SmsText(string rendered)
    {
        var plain = MarkdownLink().Replace(rendered, "$2").Replace("**", string.Empty, StringComparison.Ordinal);
        return Regex.Replace(plain, @"\n{2,}", "\n").Trim();
    }

    private static NotificationPreferenceView View(NotificationPreferenceEntity p) => new(p.Email, p.Sms, p.InApp, p.Push, p.Phone, p.Lang);

    [GeneratedRegex(@"^(?:\+?880|0)(1[3-9]\d{8})$")]
    private static partial Regex BdMobile();

    [GeneratedRegex(@"[\s\-().]")]
    private static partial Regex SeparatorChars();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownLink();
}

public interface ISmsTransport
{
    Task SendAsync(SmsMessageEntity message, CancellationToken ct);
}

public sealed partial class LoggingSmsTransport(ILogger<LoggingSmsTransport> logger) : ISmsTransport
{
    public Task SendAsync(SmsMessageEntity message, CancellationToken ct)
    {
        Log(logger, message.Id, message.Segments);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SMS #{Id} ({Segments} segment(s)) handed to the logging transport; nothing is sent until a Bangladeshi SMS provider is configured.")]
    private static partial void Log(ILogger logger, long id, int segments);
}

/// <summary>Hands queued SMS to the transport. Public so tests can drive one pass.</summary>
public sealed partial class SmsDispatcher(IServiceScopeFactory scopes, TimeProvider time, IOptions<ChannelOptions> options, ILogger<SmsDispatcher> logger) : BackgroundService
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
                await DispatchDueAsync(50, stoppingToken);
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

    public async Task<int> DispatchDueAsync(int batch, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
        var transport = scope.ServiceProvider.GetRequiredService<ISmsTransport>();
        var sent = 0;
        foreach (var message in await db.SmsMessages.Where(m => m.Status == "queued").OrderBy(m => m.Id).Take(batch).ToListAsync(ct))
        {
            try
            {
                await transport.SendAsync(message, ct);
                message.Status = "sent";
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

    [LoggerMessage(Level = LogLevel.Error, Message = "SMS dispatch cycle failed; retrying at the next poll.")]
    private static partial void CycleFailed(ILogger logger, Exception exception);
}
