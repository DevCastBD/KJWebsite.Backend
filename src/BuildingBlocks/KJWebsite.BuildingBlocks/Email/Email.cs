using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace KJWebsite.BuildingBlocks.Email;

public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody, string? Tag = null);

/// <summary>Provider-agnostic sender (ADR-007). Throw on failure: callers run inside the outbox, which retries.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary><c>Postmark</c> or <c>File</c>. Empty: <c>File</c> in Development, <c>Postmark</c> everywhere else.</summary>
    public string? Provider { get; set; }

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Kolpojontro";

    /// <summary>Where admin notifications go. Empty disables them.</summary>
    public string? OrgInbox { get; set; }

    /// <summary>Language of admin notifications (<c>bn</c> or <c>en</c>).</summary>
    public string OrgLanguage { get; set; } = "bn";

    public PostmarkOptions Postmark { get; set; } = new();

    public FileOptions File { get; set; } = new();
}

public sealed class PostmarkOptions
{
    /// <summary>Secret. Environment variable <c>Email__Postmark__ServerToken</c>; never committed.</summary>
    public string? ServerToken { get; set; }

    public string MessageStream { get; set; } = "outbound";

    public string BaseUrl { get; set; } = "https://api.postmarkapp.com/";
}

public sealed class FileOptions
{
    /// <summary>Development sink: each message is written here as an .html and .txt pair.</summary>
    public string Directory { get; set; } = Path.Combine(Path.GetTempPath(), "kj-mail");
}

public sealed class PostmarkEmailSender(HttpClient http, IOptions<EmailOptions> options) : IEmailSender
{
    // Postmark documents PascalCase members; send them exactly as documented.
    private static readonly System.Text.Json.JsonSerializerOptions PostmarkJson = new() { PropertyNamingPolicy = null };

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "email")
        {
            Content = JsonContent.Create(new
            {
                From = $"{settings.FromName} <{settings.FromAddress}>",
                To = message.To,
                Subject = message.Subject,
                HtmlBody = message.HtmlBody,
                TextBody = message.TextBody,
                MessageStream = settings.Postmark.MessageStream,
                Tag = message.Tag
            }, options: PostmarkJson)
        };
        request.Headers.Add("X-Postmark-Server-Token", settings.Postmark.ServerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Status only: the response echoes the recipient address.
            throw new HttpRequestException($"Postmark rejected the message with status {(int)response.StatusCode}.");
        }
    }
}

/// <summary>Development sender: nothing leaves the machine, so nobody sends test mail to a real applicant.</summary>
public sealed class FileEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var directory = options.Value.File.Directory;
        Directory.CreateDirectory(directory);
        var name = $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
        var header = $"To: {message.To}\nSubject: {message.Subject}\n\n";
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".txt"), header + message.TextBody, Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".html"), message.HtmlBody, Encoding.UTF8, cancellationToken);
    }
}

public static class EmailRegistration
{
    public static IServiceCollection AddKjEmail(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        var settings = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();
        var provider = string.IsNullOrWhiteSpace(settings.Provider) ? (environment.IsDevelopment() ? "File" : "Postmark") : settings.Provider;

        if (string.Equals(provider, "File", StringComparison.OrdinalIgnoreCase))
        {
            return services.AddSingleton<IEmailSender, FileEmailSender>();
        }

        if (!string.Equals(provider, "Postmark", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Email:Provider '{provider}' is not supported (use Postmark or File).");
        }

        if (!configuration.GetValue<bool>("OpenApi:Export"))
        {
            // Fail at startup, not on the first applicant's acknowledgement.
            if (string.IsNullOrWhiteSpace(settings.Postmark.ServerToken) || string.IsNullOrWhiteSpace(settings.FromAddress))
            {
                throw new InvalidOperationException("Email:Postmark:ServerToken and Email:FromAddress are required outside Development.");
            }
        }

        services.AddHttpClient<IEmailSender, PostmarkEmailSender>((sp, client) =>
        {
            client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<EmailOptions>>().Value.Postmark.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        return services;
    }
}
