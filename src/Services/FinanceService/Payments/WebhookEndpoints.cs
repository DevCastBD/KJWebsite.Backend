using System.Text;
using FinanceService.Data;
using FinanceService.Donations;
using KJWebsite.BuildingBlocks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace FinanceService.Payments;

/// <summary>
/// Where gateways call us. Anonymous by necessity, so safety lives in <see cref="WebhookProcessor"/>: signature first, then the gateway's own status answer.
/// A server-to-server notification (POST) is answered 200, 400 or 503; a donor's browser coming back (GET) is sent on to the site's status page.
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhooks(this IEndpointRouteBuilder app)
    {
        var handler = async (HttpContext context, string gateway, WebhookProcessor processor, FinanceDbContext db, IOptions<PaymentOptions> payments) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = await processor.ProcessAsync(gateway, await ReadAsync(context), context.RequestAborted);

            if (result.Disposition == WebhookDisposition.Rejected)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "WEBHOOK_REJECTED", "The call was not accepted.");
            }

            if (HttpMethods.IsGet(context.Request.Method))
            {
                // The donor's browser: whatever was decided, the status page shows the truth. A retry just means it is still pending there.
                context.Response.Headers.Location = result.Reference is null
                    ? $"{payments.Value.SiteBaseUrl.TrimEnd('/')}/en/donate/status"
                    : await PublicDonationEndpoints.StatusPageUrlAsync(db, payments.Value, result.Reference, "return");
                return Results.StatusCode(StatusCodes.Status303SeeOther);
            }

            if (result.Disposition == WebhookDisposition.Retry)
            {
                context.Response.Headers.RetryAfter = "30";
                return ApiError.Create(context, StatusCodes.Status503ServiceUnavailable, "TEMPORARILY_UNAVAILABLE", "The payment could not be checked right now; please deliver again.");
            }

            return Results.Ok(new { received = true });
        };

        foreach (var method in new[] { HttpMethods.Get, HttpMethods.Post })
        {
            app.MapMethods("/api/v1/donations/webhooks/{gateway}", [method], handler)
                .WithName($"PaymentWebhook{method[0]}{method[1..].ToLowerInvariant()}")
                .WithSummary("Payment gateway webhook or browser callback")
                .WithDescription("Called by the gateways, not by the site. The call is a prompt to look, never proof: the signature is checked where the gateway signs (unsigned or badly signed calls get 400 and are logged), then the gateway itself is asked what happened, and only its answer confirms or fails the donation. A repeated delivery changes nothing. POST answers 200, 400 or 503 (deliver again); GET, the donor returning, answers 303 to the site's status page.")
                .WithTags("Donations")
                .AllowAnonymous();
        }
    }

    private const int MaxBody = 1_000_000;

    private static async Task<IncomingWebhook> ReadAsync(HttpContext context)
    {
        var query = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
        var headers = context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString(), StringComparer.Ordinal);

        // The body is read once as text: signatures such as Stripe's cover its exact bytes, and form bodies are parsed from the same text.
        var raw = string.Empty;
        if (!HttpMethods.IsGet(context.Request.Method) && context.Request.ContentLength is null or <= MaxBody)
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                var buffer = new char[MaxBody + 1];
                var read = await reader.ReadBlockAsync(buffer, context.RequestAborted);
                raw = read > MaxBody ? string.Empty : new string(buffer, 0, read);
            }
            catch (Exception exception) when (exception is IOException or BadHttpRequestException or DecoderFallbackException)
            {
                // An unreadable body is treated as an empty one, which the adapter then refuses.
            }
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        if (raw.Length > 0 && context.Request.HasFormContentType)
        {
            foreach (var field in QueryHelpers.ParseQuery(raw))
            {
                form[field.Key] = field.Value.ToString();
            }
        }

        return new IncomingWebhook(context.Request.Method, query, form, context.Connection.RemoteIpAddress?.ToString() ?? "unknown", headers, raw);
    }
}
