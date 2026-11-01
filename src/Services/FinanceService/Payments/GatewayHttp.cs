using System.Net;
using System.Text.Json;

namespace FinanceService.Payments;

/// <summary>Shared plumbing so every adapter classifies failures the same way: transient problems are "unavailable", a refusal is "rejected".</summary>
internal static class GatewayHttp
{
    public static HttpClient Client(IHttpClientFactory factory, string name, int timeoutSeconds)
    {
        var client = factory.CreateClient(name);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60));
        return client;
    }

    public static async Task<JsonElement> SendForJsonAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayUnavailableException("GATEWAY_TIMEOUT", "The gateway did not answer in time.");
        }
        catch (HttpRequestException exception)
        {
            throw new GatewayUnavailableException("GATEWAY_UNREACHABLE", "The gateway could not be reached.", exception);
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500)
            {
                throw new GatewayUnavailableException($"GATEWAY_HTTP_{(int)response.StatusCode}", "The gateway reported an error.");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // A 4xx means the gateway refused the call: wrong credentials or a request it will never accept.
                throw response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                    ? new GatewayUnavailableException($"GATEWAY_HTTP_{(int)response.StatusCode}", "The gateway is busy.")
                    : new GatewayRejectedException($"GATEWAY_HTTP_{(int)response.StatusCode}", "The gateway refused the request.");
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new GatewayUnavailableException("GATEWAY_BAD_RESPONSE", "The gateway answered with something unreadable.", exception);
            }
        }
    }

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The redirect URL must be https (plain http only in sandbox mode, where some gateways still use it) so a hostile or broken answer cannot send a donor somewhere odd.</summary>
    public static Uri RedirectUrl(string? value, string code, bool allowHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new GatewayUnavailableException(code, "The gateway did not return a usable checkout address.");
        }

        return uri;
    }
}
