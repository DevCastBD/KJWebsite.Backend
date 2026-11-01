using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KJWebsite.IntegrationTests;

public sealed record RecordedRequest(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public Dictionary<string, string> Form => Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <summary>A scripted stand-in for a payment gateway's HTTP API: records every request and answers with whatever the test supplies.</summary>
public sealed class ScriptedGatewayHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests { get { lock (_requests) { return [.. _requests]; } } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body);
        lock (_requests)
        {
            _requests.Add(recorded);
        }

        return respond(recorded);
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

/// <summary>
/// A test double for Nagad that does the real RSA work: it opens what the adapter encrypted to its key, checks the merchant signature, and encrypts
/// its answers to the merchant key. This proves the adapter's crypto is internally consistent, not that it matches Nagad's live service.
/// </summary>
public sealed class NagadDouble : IDisposable
{
    public RSA Merchant { get; } = RSA.Create(2048);
    public RSA Gateway { get; } = RSA.Create(2048);
    public string Challenge { get; } = "server-challenge-123";
    public List<string> Problems { get; } = [];
    public List<JsonElement> Initialized { get; } = [];
    public List<JsonElement> Completed { get; } = [];

    public ScriptedGatewayHandler Handler => new(Respond);

    public HttpResponseMessage Respond(RecordedRequest request)
    {
        var path = request.Url.AbsolutePath;
        var envelope = request.Json;
        var sensitive = envelope.GetProperty("sensitiveData").GetString()!;
        var plain = Gateway.Decrypt(Convert.FromBase64String(sensitive), RSAEncryptionPadding.Pkcs1);
        var signature = Convert.FromBase64String(envelope.GetProperty("signature").GetString()!);
        if (!Merchant.VerifyData(plain, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            Problems.Add("bad signature");
        }

        foreach (var header in new[] { "x-km-api-version", "x-km-ip-v4", "x-km-client-type" })
        {
            if (!request.Headers.ContainsKey(header))
            {
                Problems.Add("missing " + header);
            }
        }

        var data = JsonDocument.Parse(plain).RootElement.Clone();
        if (path.Contains("/check-out/initialize/", StringComparison.Ordinal))
        {
            Initialized.Add(data);
            var answer = JsonSerializer.SerializeToUtf8Bytes(new { paymentReferenceId = "PR-NAGAD-1", challenge = Challenge });
            return ScriptedGatewayHandler.Json(new
            {
                sensitiveData = Convert.ToBase64String(Merchant.Encrypt(answer, RSAEncryptionPadding.Pkcs1)),
                signature = Convert.ToBase64String(Gateway.SignData(answer, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            });
        }

        Completed.Add(data);
        return ScriptedGatewayHandler.Json(new { status = "Success", callBackUrl = "https://sandbox.mynagad.com/pay/PR-NAGAD-1" });
    }

    public void Dispose()
    {
        Merchant.Dispose();
        Gateway.Dispose();
    }
}
