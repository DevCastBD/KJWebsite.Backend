using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace ContentService.Data;

public sealed class EventOptions
{
    public const string SectionName = "Events";

    /// <summary>Base64 key (at least 32 bytes) for the keyed email hash. Required outside Development.</summary>
    public string HashKey { get; set; } = string.Empty;
}

/// <summary>Field encryption for guest personal data and the keyed email hash used to spot a repeat registration without storing the email in the clear.</summary>
public sealed class RegistrationPrivacy
{
    private readonly IDataProtector _protector;
    private readonly byte[] _key;

    public RegistrationPrivacy(IDataProtectionProvider provider, Microsoft.Extensions.Options.IOptions<EventOptions> options, IHostEnvironment environment)
    {
        _protector = provider.CreateProtector("KJWebsite.Content.EventRegistration.v1");
        var configured = options.Value.HashKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Events:HashKey is required (base64, at least 32 bytes).");
            }

            configured = Convert.ToBase64String(Encoding.UTF8.GetBytes("kj-events-development-hash-key-only-!"));
        }

        _key = Convert.FromBase64String(configured);
        if (_key.Length < 32)
        {
            throw new InvalidOperationException("Events:HashKey must decode to at least 32 bytes.");
        }
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string value) => _protector.Unprotect(value);

    public string HashEmail(string email) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("email:" + email.Trim().ToLowerInvariant()))).ToLowerInvariant();
}
