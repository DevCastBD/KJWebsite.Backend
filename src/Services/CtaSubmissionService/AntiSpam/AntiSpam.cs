using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CtaSubmissionService.AntiSpam;

public sealed class AntiSpamOptions
{
    public const string SectionName = "AntiSpam";

    /// <summary>Hidden form field a human never fills. Any value rejects the submission.</summary>
    public string HoneypotField { get; set; } = "website";

    /// <summary>A form submitted faster than this after it was shown is a script. Only checked when the client sends <c>formOpenedAt</c>.</summary>
    public int MinFillSeconds { get; set; } = 3;

    public TurnstileOptions Turnstile { get; set; } = new();
}

public sealed class TurnstileOptions
{
    /// <summary>Secret. Empty disables the check (the default); then <c>turnstileToken</c> is ignored.</summary>
    public string? SecretKey { get; set; }

    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
}

public enum SpamVerdict
{
    Accepted,
    Honeypot,
    TooFast,
    ChallengeFailed
}

public interface ITurnstileVerifier
{
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken);
}

/// <summary>Cloudflare Turnstile. Fails open when Cloudflare itself is unreachable: a spam filter must not take the volunteer form down.</summary>
public sealed partial class TurnstileVerifier(HttpClient http, IOptions<AntiSpamOptions> options, ILogger<TurnstileVerifier> logger) : ITurnstileVerifier
{
    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken)
    {
        var settings = options.Value.Turnstile;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            using var response = await http.PostAsync(settings.VerifyUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = settings.SecretKey!,
                ["response"] = token,
                ["remoteip"] = remoteIp ?? string.Empty
            }), cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<TurnstileResponse>(cancellationToken);
            return result?.Success == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogUnavailable(ex.GetType().Name);
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Turnstile verification unavailable ({Reason}); letting the submission through")]
    private partial void LogUnavailable(string reason);

    private sealed record TurnstileResponse([property: JsonPropertyName("success")] bool Success);
}

public sealed class SubmissionGuard(IOptions<AntiSpamOptions> options, ITurnstileVerifier turnstile)
{
    public async Task<SpamVerdict> CheckAsync(IReadOnlyDictionary<string, object?> values, TimeSpan? fillTime, string? turnstileToken, string? remoteIp, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (values.TryGetValue(settings.HoneypotField, out var trap) && !string.IsNullOrWhiteSpace(trap?.ToString()))
        {
            return SpamVerdict.Honeypot;
        }

        // fillTime is submittedAt - formOpenedAt, both from the client's clock, so clock skew cancels out.
        if (fillTime is { } elapsed && elapsed < TimeSpan.FromSeconds(settings.MinFillSeconds))
        {
            return SpamVerdict.TooFast;
        }

        if (!string.IsNullOrWhiteSpace(settings.Turnstile.SecretKey) && !await turnstile.VerifyAsync(turnstileToken, remoteIp, cancellationToken))
        {
            return SpamVerdict.ChallengeFailed;
        }

        return SpamVerdict.Accepted;
    }
}
