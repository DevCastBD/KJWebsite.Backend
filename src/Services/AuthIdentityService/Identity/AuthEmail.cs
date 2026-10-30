using System.Collections.Concurrent;

namespace AuthIdentityService.Identity;

public enum AuthEmailKind
{
    ConfirmEmail,
    ResetPassword
}

public sealed record AuthEmail(AuthEmailKind Kind, string UserId, string To, string Token);

/// <summary>Delivers account emails. Real delivery arrives with the outbox in #32.</summary>
public interface IAuthEmailSender
{
    Task SendAsync(AuthEmail email, CancellationToken cancellationToken = default);
}

/// <summary>
/// Holds the most recent messages in memory instead of sending them. Content (address, token) is logged
/// only when <paramref name="logContent"/> is set, which is Development only: PII never reaches production logs.
/// </summary>
public sealed partial class InMemoryAuthEmailSender(ILogger<InMemoryAuthEmailSender> logger, bool logContent) : IAuthEmailSender
{
    private const int Capacity = 100;
    private readonly ConcurrentQueue<AuthEmail> _sent = new();

    public IReadOnlyCollection<AuthEmail> Sent => _sent.ToArray();

    public Task SendAsync(AuthEmail email, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(email);
        while (_sent.Count > Capacity)
        {
            _sent.TryDequeue(out _);
        }

        if (logContent)
        {
            LogContent(email.Kind, email.To, email.UserId, email.Token);
        }
        else
        {
            LogNotDelivered(email.Kind);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auth email {Kind} to {To}: user {UserId} token {Token}")]
    private partial void LogContent(AuthEmailKind kind, string to, string userId, string token);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auth email {Kind} was not delivered: no email provider is configured yet (#32).")]
    private partial void LogNotDelivered(AuthEmailKind kind);
}
