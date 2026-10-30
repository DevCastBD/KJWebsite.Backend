using System.Net.Mail;
using System.Text.Json;
using CtaSubmissionService.Data;
using CtaSubmissionService.Email;
using KJWebsite.BuildingBlocks.Email;
using KJWebsite.BuildingBlocks.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CtaSubmissionService.Outbox;

public static class OutboxTypes
{
    public const string Email = "cta.email";
}

public sealed record CtaEmailPayload(string SubmissionId, CtaEmailKind Kind);

public static class CtaEmailPlanner
{
    /// <summary>
    /// Queues the emails a submission triggers. Called before the submission's own <c>SaveChanges</c>, so they commit with it.
    /// The acknowledgement needs a usable address from the form; the organisation notice needs a configured inbox.
    /// </summary>
    public static void Enqueue(CtaDbContext db, string submissionId, string formName, IReadOnlyDictionary<string, object?> values, EmailOptions options)
    {
        if (ExtractAddress(values) is not null)
        {
            var kind = string.Equals(formName, "registration", StringComparison.OrdinalIgnoreCase) ? CtaEmailKind.RegistrationReceived : CtaEmailKind.ContactAck;
            db.Enqueue(OutboxTypes.Email, new CtaEmailPayload(submissionId, kind));
        }

        if (!string.IsNullOrWhiteSpace(options.OrgInbox))
        {
            db.Enqueue(OutboxTypes.Email, new CtaEmailPayload(submissionId, CtaEmailKind.AdminNotification));
        }
    }

    internal static string? ExtractAddress(IReadOnlyDictionary<string, object?> values) =>
        values.TryGetValue("email", out var raw) && raw?.ToString()?.Trim() is { Length: > 0 and <= 254 } text && MailAddress.TryCreate(text, out var address) && address.Address == text
            ? text
            : null;
}

/// <summary>
/// Sends one email for one outbox message. The payload holds only the submission id and kind; the personal data is read
/// here, at send time, so it never sits in the outbox table or a job dashboard. Delivery is at-least-once.
/// </summary>
public sealed class CtaEmailHandler(IServiceScopeFactory scopes, IEmailSender sender, IOptions<EmailOptions> options) : IOutboxHandler
{
    public string MessageType => OutboxTypes.Email;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<CtaEmailPayload>(message.Payload, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Outbox email payload is empty.");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CtaDbContext>();
        var submission = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == payload.SubmissionId, cancellationToken)
            ?? throw new InvalidOperationException($"Submission {payload.SubmissionId} no longer exists.");

        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(submission.ValuesJson) ?? [];
        var data = new CtaEmailData(
            submission.Id,
            submission.FormName,
            Name: Join(Read(values, "firstName"), Read(values, "lastName")) ?? Read(values, "name") ?? Read(values, "fullName"),
            Email: CtaEmailPlanner.ExtractAddress(values),
            Phone: Read(values, "phone") ?? Read(values, "phoneNumber") ?? Read(values, "mobile"),
            Message: Read(values, "message"),
            submission.SubmittedAt);

        var settings = options.Value;
        var (to, language) = payload.Kind == CtaEmailKind.AdminNotification
            ? (settings.OrgInbox, settings.OrgLanguage)
            : (data.Email, submission.Language);
        if (string.IsNullOrWhiteSpace(to))
        {
            return; // nothing to deliver to (address removed or inbox no longer configured); retrying cannot help
        }

        await sender.SendAsync(CtaEmailTemplates.Render(payload.Kind, language, to, data), cancellationToken);
    }

    private static string? Read(Dictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var raw) && raw is not null && raw.ToString()?.Trim() is { Length: > 0 } text ? text : null;

    private static string? Join(string? first, string? last) =>
        string.Join(' ', new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } joined ? joined : null;
}
