using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KJWebsite.BuildingBlocks.Security;

public sealed record SessionCheckOptions(bool Enabled, string BaseAddress, TimeSpan Timeout)
{
    public static SessionCheckOptions From(IConfiguration configuration) => new(
        configuration.GetValue("Auth:SessionCheck:Enabled", true),
        configuration["Services:Auth:Url"] ?? "http://localhost:7003/",
        configuration.GetValue("Auth:SessionCheck:Timeout", TimeSpan.FromSeconds(2)));
}

public static partial class SessionCheck
{
    public const string ClientName = "kj-session-check";
    public const string UnavailableKey = "KJWebsite.SessionCheck.Unavailable";

    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var http = context.HttpContext;
        var aborted = http.RequestAborted;
        try
        {
            var options = SessionCheckOptions.From(http.RequestServices.GetRequiredService<IConfiguration>());
            if (!options.Enabled)
            {
                // The switch exists for tests and local development. Anywhere else it is ignored, so one stray setting cannot bring the stale-session window back.
                if (http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()) return;
                IgnoredDisable(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SessionCheck).FullName!));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            timeout.CancelAfter(options.Timeout);
            using var client = http.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(new Uri(options.BaseAddress, UriKind.Absolute), "/api/v1/auth/sessions/current"));
            request.Headers.TryAddWithoutValidation("Authorization", http.Request.Headers.Authorization.ToArray());
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NoContent) return;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                context.Fail("The session is no longer valid.");
                return;
            }
        }
        catch (Exception) when (aborted.IsCancellationRequested)
        {
            context.Fail("The request was aborted.");
            return;
        }
        catch (Exception)
        {
            // Configuration, transport and check cancellation failures all fail closed.
        }

        if (!aborted.IsCancellationRequested)
        {
            http.Items[UnavailableKey] = true;
            Unavailable(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SessionCheck).FullName!));
        }
        context.Fail("The session check could not be completed.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auth:SessionCheck:Enabled=false is ignored outside Development; the session check stays on.")]
    private static partial void IgnoredDisable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in service is unavailable.")]
    private static partial void Unavailable(ILogger logger);
}
