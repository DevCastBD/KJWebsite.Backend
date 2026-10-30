using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace ContentService;

/// <summary>
/// Preview links to unpublished content. A token is `base64url(itemId|expiry).base64url(HMAC-SHA256)`: it names exactly one item, stops
/// working at its expiry, and cannot be altered or moved to another item. The key is `Preview:SigningKey`; Development generates an
/// ephemeral one (links then die on restart), any other environment must configure it.
/// </summary>
public sealed class PreviewTokens(IConfiguration configuration, IHostEnvironment environment, TimeProvider time)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7);

    private readonly Lazy<byte[]?> _key = new(() =>
    {
        if (configuration["Preview:SigningKey"] is { Length: >= 16 } configured)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        }

        return environment.IsDevelopment() ? RandomNumberGenerator.GetBytes(32) : null;
    });

    public bool IsConfigured => _key.Value is not null;

    public (string Token, DateTimeOffset ExpiresAt) Create(string itemId, TimeSpan lifetime)
    {
        var expires = time.GetUtcNow().Add(lifetime);
        var payload = Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes($"{itemId}|{expires.ToUnixTimeSeconds()}"));
        return ($"{payload}.{Sign(payload)}", expires);
    }

    /// <summary>The item id the token names, or null when it is malformed, altered or expired.</summary>
    public string? Validate(string token)
    {
        var parts = token.Split('.');
        if (!IsConfigured || parts.Length != 2 || parts[0].Length is 0 or > 300)
        {
            return null;
        }

        byte[] given;
        try
        {
            given = Base64UrlTextEncoder.Decode(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(given, Mac(parts[0])))
        {
            return null;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64UrlTextEncoder.Decode(parts[0]));
        }
        catch (FormatException)
        {
            return null;
        }

        var cut = text.LastIndexOf('|');
        return cut > 0 && long.TryParse(text[(cut + 1)..], out var expiry) && expiry > time.GetUtcNow().ToUnixTimeSeconds() ? text[..cut] : null;
    }

    private string Sign(string payload) => Base64UrlTextEncoder.Encode(Mac(payload));

    private byte[] Mac(string payload) => HMACSHA256.HashData(_key.Value!, Encoding.UTF8.GetBytes(payload));
}
