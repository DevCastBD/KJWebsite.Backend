using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KJWebsite.BuildingBlocks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReportingService.Data;
using ReportingService.Data.Entities;

namespace ReportingService.Reports;

public sealed class ReportOptions
{
    public const string SectionName = "Reporting:Reports";

    /// <summary>How many times a scheduled run is generated and delivered before it is recorded as failed for this tick.</summary>
    public int Attempts { get; set; } = 3;

    public int RetryDelaySeconds { get; set; } = 5;

    /// <summary>After a failed tick the schedule is due again this many minutes later, and keeps being retried until it succeeds.</summary>
    public int RetryMinutes { get; set; } = 60;

    public bool WorkerEnabled { get; set; } = true;
    public int TickSeconds { get; set; } = 60;
    public int MaxRecipients { get; set; } = 50;
}

public sealed record ReportAttachment(string FileName, string ContentType, byte[] Content);

public sealed record ReportMail(string To, string Subject, string Body, IReadOnlyList<ReportAttachment> Attachments);

public interface IReportMailer
{
    Task<Result> SendAsync(ReportMail mail, CancellationToken ct);
}

/// <summary>Used until `Reporting:Smtp:Host` is configured. It refuses every send, so a scheduled report fails loudly instead of looking delivered.</summary>
public sealed class NotConfiguredReportMailer : IReportMailer
{
    public Task<Result> SendAsync(ReportMail mail, CancellationToken ct) =>
        Task.FromResult(Result.Failure("MAIL_NOT_CONFIGURED", "No mail server is configured (Reporting:Smtp:Host), so the report could not be sent."));
}

/// <summary>Sends through the SMTP server named in `Reporting:Smtp` (Host, Port, UseSsl, User, Password, From).</summary>
public sealed class SmtpReportMailer(IConfiguration configuration) : IReportMailer
{
    public async Task<Result> SendAsync(ReportMail mail, CancellationToken ct)
    {
        var section = configuration.GetSection("Reporting:Smtp");
        try
        {
            using var message = new MailMessage(section["From"] ?? "reports@localhost", mail.To, mail.Subject, mail.Body) { BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8 };
            foreach (var attachment in mail.Attachments)
            {
                message.Attachments.Add(new Attachment(new MemoryStream(attachment.Content), attachment.FileName, attachment.ContentType));
            }

            using var client = new SmtpClient(section["Host"], section.GetValue("Port", 587)) { EnableSsl = section.GetValue("UseSsl", true) };
            if (section["User"] is { Length: > 0 } user)
            {
                client.Credentials = new System.Net.NetworkCredential(user, section["Password"]);
            }

            await client.SendMailAsync(message, ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException or IOException)
        {
            return Result.Failure("MAIL_FAILED", ex.Message);
        }
    }
}

public sealed record ScheduleRequest(int? DayOfMonth, int? HourUtc, string[]? Formats, Dictionary<string, string>? Parameters);

public sealed record RecipientRequest(string? Email);

public sealed record RecipientView(string Id, string Email, string? AddedBy, DateTimeOffset AddedAt);

public sealed record ScheduleView(string Id, string ReportId, int DayOfMonth, int HourUtc, IReadOnlyList<string> Formats, IReadOnlyDictionary<string, string> Parameters, bool Active, DateTimeOffset NextRunAt,
    DateTimeOffset? LastSuccessAt, int FailureCount, IReadOnlyList<RecipientView> Recipients);

public sealed record ScheduleListView(IReadOnlyList<ScheduleView> Items);

public sealed record DueRunResult(int Due, int Delivered, int Failed);

/// <summary>
/// Monthly delivery of saved reports. A schedule is due from its next run time until a run succeeds: a failed generation or send is logged as an alert and tried again after
/// `RetryMinutes`, and a retry delivers only to the people who have not yet received that month's report, from the files already generated. Nothing is skipped silently.
/// </summary>
public sealed class ReportScheduler(ReportingDbContext db, ReportService reports, IDataProtectionProvider protection, IReportMailer mailer, IOptions<ReportOptions> options, TimeProvider clock, ILogger<ReportScheduler> logger)
{
    private static readonly Action<ILogger, string, string, Exception?> DeliveryFailed = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(41, nameof(DeliveryFailed)), "ALERT: scheduled report {ScheduleId} was not delivered and will be retried: {Reason}");

    private IDataProtector Protector => protection.CreateProtector("Reporting.Recipients");

    public static DateTimeOffset NextRun(DateTimeOffset after, int dayOfMonth, int hourUtc)
    {
        var candidate = new DateTimeOffset(after.Year, after.Month, dayOfMonth, hourUtc, 0, 0, TimeSpan.Zero);
        return candidate > after ? candidate : candidate.AddMonths(1);
    }

    public async Task<Result<ReportScheduleEntity>> CreateAsync(ReportDefinitionEntity report, ScheduleRequest request, string? actor, CancellationToken ct)
    {
        if (request.DayOfMonth is not (>= 1 and <= 28))
        {
            return Result.Failure<ReportScheduleEntity>("VALIDATION_ERROR", "dayOfMonth must be 1-28, so the report runs every month.");
        }

        if (request.HourUtc is not (>= 0 and <= 23))
        {
            return Result.Failure<ReportScheduleEntity>("VALIDATION_ERROR", "hourUtc must be 0-23.");
        }

        var formats = (request.Formats is { Length: > 0 } ? request.Formats : [ReportService.Xlsx]).Select(f => f.ToLowerInvariant()).Distinct().ToList();
        if (formats.FirstOrDefault(f => !ReportService.Formats.Contains(f)) is { } bad)
        {
            return Result.Failure<ReportScheduleEntity>("VALIDATION_ERROR", $"Unknown format '{bad}'. Use csv, xlsx or pdf.");
        }

        var parameters = request.Parameters ?? [];
        var merged = ReportTemplates.ReadParameters(report.ParametersJson);
        foreach (var pair in parameters)
        {
            merged[pair.Key] = pair.Value;
        }

        if (ReportTemplates.IsSensitive(report.Template, merged.GetValueOrDefault("metric")) && !report.Sensitive)
        {
            return Result.Failure<ReportScheduleEntity>("FORBIDDEN", "These parameters turn a general report into a financial one; save it as its own report.");
        }

        var now = clock.GetUtcNow();
        var schedule = new ReportScheduleEntity
        {
            Id = $"sch_{Guid.NewGuid():N}", ReportId = report.Id, DayOfMonth = request.DayOfMonth.Value, HourUtc = request.HourUtc.Value, Formats = string.Join(',', formats),
            ParametersJson = JsonSerializer.Serialize(parameters), NextRunAt = NextRun(now, request.DayOfMonth.Value, request.HourUtc.Value), CreatedBy = actor, CreatedAt = now
        };
        db.ReportSchedules.Add(schedule);
        await db.SaveChangesAsync(ct);
        return Result.Success(schedule);
    }

    public async Task<Result<RecipientView>> AddRecipientAsync(ReportScheduleEntity schedule, string? email, string? actor, CancellationToken ct)
    {
        var address = email?.Trim() ?? string.Empty;
        if (address.Length is 0 or > 254 || !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address || !address.Contains('@', StringComparison.Ordinal))
        {
            return Result.Failure<RecipientView>("VALIDATION_ERROR", "email must be a valid address.");
        }

        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address.ToLowerInvariant())));
        var current = await db.ReportRecipients.Where(r => r.ScheduleId == schedule.Id && r.RemovedAt == null).ToListAsync(ct);
        if (current.Any(r => r.EmailKey == key))
        {
            return Result.Failure<RecipientView>("DUPLICATE", "This address already receives the report.");
        }

        if (current.Count >= options.Value.MaxRecipients)
        {
            return Result.Failure<RecipientView>("LIMIT", $"A schedule can have at most {options.Value.MaxRecipients} recipients.");
        }

        var recipient = new ReportRecipientEntity { Id = $"rcp_{Guid.NewGuid():N}", ScheduleId = schedule.Id, EmailProtected = Protector.Protect(address), EmailKey = key, AddedBy = actor, AddedAt = clock.GetUtcNow() };
        db.ReportRecipients.Add(recipient);
        await db.SaveChangesAsync(ct);
        return Result.Success(new RecipientView(recipient.Id, address, actor, recipient.AddedAt));
    }

    public async Task<Result> RemoveRecipientAsync(string scheduleId, string recipientId, string? actor, CancellationToken ct)
    {
        var recipient = await db.ReportRecipients.FirstOrDefaultAsync(r => r.Id == recipientId && r.ScheduleId == scheduleId && r.RemovedAt == null, ct);
        if (recipient is null)
        {
            return Result.Failure("NOT_FOUND", "No such recipient.");
        }

        recipient.RemovedAt = clock.GetUtcNow();
        recipient.RemovedBy = actor;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<ScheduleView> ViewAsync(ReportScheduleEntity s, CancellationToken ct)
    {
        var recipients = (await db.ReportRecipients.AsNoTracking().Where(r => r.ScheduleId == s.Id && r.RemovedAt == null).ToListAsync(ct)).OrderBy(r => r.AddedAt)
            .Select(r => new RecipientView(r.Id, Protector.Unprotect(r.EmailProtected), r.AddedBy, r.AddedAt)).ToList();
        return new ScheduleView(s.Id, s.ReportId, s.DayOfMonth, s.HourUtc, s.Formats.Split(','), ReportTemplates.ReadParameters(s.ParametersJson), s.Active, s.NextRunAt, s.LastSuccessAt, s.FailureCount, recipients);
    }

    public async Task<DueRunResult> RunDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = (await db.ReportSchedules.Where(s => s.Active).ToListAsync(ct)).Where(s => s.NextRunAt <= now).ToList();
        int delivered = 0, failed = 0;
        foreach (var schedule in due)
        {
            if (await ExecuteAsync(schedule, ct))
            {
                delivered++;
            }
            else
            {
                failed++;
            }
        }

        return new DueRunResult(due.Count, delivered, failed);
    }

    private async Task<bool> ExecuteAsync(ReportScheduleEntity schedule, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var report = await db.ReportDefinitions.FirstAsync(r => r.Id == schedule.ReportId, ct);
        var failingSince = schedule.FailingSince ?? now;
        var reason = string.Empty;

        // A previous tick may have generated the files and failed only at the send: reuse them, so a retry never makes a second, different report for the same month.
        var run = (await db.ReportRuns.Where(r => r.ScheduleId == schedule.Id && r.Status == RunStatuses.Succeeded).ToListAsync(ct)).Where(r => r.StartedAt >= failingSince).OrderByDescending(r => r.StartedAt).FirstOrDefault();
        if (schedule.FailingSince is null)
        {
            run = null;
        }

        var attempts = Math.Max(1, options.Value.Attempts);
        for (var attempt = 1; run is null && attempt <= attempts; attempt++)
        {
            var defaults = ReportTemplates.ReadParameters(schedule.ParametersJson);
            var merged = ReportTemplates.ReadParameters(report.ParametersJson);
            if (!defaults.Keys.Concat(merged.Keys).Any(k => k is "month" or "from" or "to" or "months"))
            {
                var previous = now.AddMonths(-1);
                defaults["month"] = $"{previous.Year:0000}-{previous.Month:00}";
            }

            var made = await reports.RunAsync(report, null, schedule.Formats.Split(','), RunTriggers.Schedule, schedule.Id, defaults, null, ct);
            if (made.IsSuccess)
            {
                run = made.Value!;
                run.Attempts = attempt;
                await db.SaveChangesAsync(ct);
            }
            else
            {
                reason = made.Error!.Message;
                if (attempt < attempts && options.Value.RetryDelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.Value.RetryDelaySeconds), clock, ct);
                }
            }
        }

        if (run is not null)
        {
            reason = await DeliverAsync(schedule, report, run, ct);
        }

        if (reason.Length == 0)
        {
            schedule.LastSuccessAt = now;
            schedule.FailureCount = 0;
            schedule.FailingSince = null;
            schedule.NextRunAt = NextRun(now, schedule.DayOfMonth, schedule.HourUtc);
            await db.SaveChangesAsync(ct);
            return true;
        }

        schedule.FailureCount++;
        schedule.FailingSince = failingSince;
        schedule.NextRunAt = now.AddMinutes(Math.Max(1, options.Value.RetryMinutes));
        await db.SaveChangesAsync(ct);
        DeliveryFailed(logger, schedule.Id, reason, null);
        return false;
    }

    /// <summary>Mails the run to every active recipient who has not been sent it yet. Returns "" when everyone has it, otherwise why not.</summary>
    private async Task<string> DeliverAsync(ReportScheduleEntity schedule, ReportDefinitionEntity report, ReportRunEntity run, CancellationToken ct)
    {
        var recipients = await db.ReportRecipients.Where(r => r.ScheduleId == schedule.Id && r.RemovedAt == null).ToListAsync(ct);
        var already = (await db.ReportDeliveries.Where(d => d.RunId == run.Id && d.Status == DeliveryStatuses.Sent).Select(d => d.RecipientId).ToListAsync(ct)).ToHashSet();
        var files = await db.ReportFiles.AsNoTracking().Where(f => f.RunId == run.Id).ToListAsync(ct);
        var attachments = files.Select(f => new ReportAttachment(f.FileName, f.ContentType, f.Content)).ToList();
        var parameters = ReportTemplates.ReadParameters(run.ParametersJson);
        var period = parameters.GetValueOrDefault("month") ?? $"{parameters.GetValueOrDefault("from")} to {parameters.GetValueOrDefault("to")}".Trim();
        var problems = new List<string>();
        foreach (var recipient in recipients.Where(r => !already.Contains(r.Id)))
        {
            var mail = new ReportMail(Protector.Unprotect(recipient.EmailProtected), $"{report.Name} ({period})", $"The {report.Name} report for {period} is attached.\n\nGenerated {clock.GetUtcNow():yyyy-MM-dd HH:mm} UTC.", attachments);
            var sent = await mailer.SendAsync(mail, ct);
            db.ReportDeliveries.Add(new ReportDeliveryEntity { RunId = run.Id, RecipientId = recipient.Id, Status = sent.IsSuccess ? DeliveryStatuses.Sent : DeliveryStatuses.Failed, Error = sent.Error?.Message, At = clock.GetUtcNow() });
            if (!sent.IsSuccess)
            {
                problems.Add($"{recipient.Id}: {sent.Error!.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        return string.Join("; ", problems);
    }
}

/// <summary>Checks for due schedules every `TickSeconds`. Each schedule logs its own failures; the worker only keeps time.</summary>
public sealed class ReportScheduleWorker(IServiceScopeFactory scopes, IOptions<ReportOptions> options, TimeProvider clock, ILogger<ReportScheduleWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Crashed = LoggerMessage.Define(LogLevel.Error, new EventId(42, nameof(Crashed)), "ALERT: the report schedule worker threw while checking for due reports");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReportScheduler>().RunDueAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.Value.TickSeconds)), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                Crashed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
            }
        }
    }
}
