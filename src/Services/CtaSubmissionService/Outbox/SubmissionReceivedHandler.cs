using KJWebsite.BuildingBlocks.Outbox;

namespace CtaSubmissionService.Outbox;

public static class OutboxTypes
{
    public const string SubmissionReceived = "cta.submission.received";
}

/// <summary>
/// Acknowledges that a submission was accepted. Email delivery (#32) replaces this with the real side effects;
/// until then the event is recorded, without personal data, so the pipeline is exercised end to end.
/// </summary>
public sealed partial class SubmissionReceivedHandler(ILogger<SubmissionReceivedHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxTypes.SubmissionReceived;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        LogReceived(message.Id);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "CTA submission event {MessageId} dispatched")]
    private partial void LogReceived(Guid messageId);
}
