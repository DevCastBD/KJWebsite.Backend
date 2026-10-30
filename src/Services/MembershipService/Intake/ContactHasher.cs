using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MembershipService.Intake;

public sealed class MembershipOptions
{
    public const string SectionName = "Membership";

    /// <summary>Base64 key for the duplicate-detection HMACs. Required outside Development.</summary>
    public string HashKey { get; set; } = string.Empty;

    /// <summary>Where attachments are written. Empty means a folder under the system temp path (development only).</summary>
    public string AttachmentsPath { get; set; } = string.Empty;

    /// <summary>What the card's QR code points at, followed by the member code. A public verification page is not built yet.</summary>
    public string CardVerifyUrl { get; set; } = "https://kolpojontro.org/verify";
}

/// <summary>
/// Keyed hashes of the normalised email and phone, so duplicates match without decrypting rows and
/// the stored values cannot be reversed from a database dump alone.
/// </summary>
public sealed class ContactHasher
{
    private readonly byte[] _key;

    public ContactHasher(IOptions<MembershipOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.HashKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Membership:HashKey is required (base64, at least 32 bytes).");
            }

            configured = Convert.ToBase64String(Encoding.UTF8.GetBytes("kj-membership-development-hash-key-only"));
        }

        _key = Convert.FromBase64String(configured);
        if (_key.Length < 32)
        {
            throw new InvalidOperationException("Membership:HashKey must decode to at least 32 bytes.");
        }
    }

    public string HashEmail(string email) => Hash("email:" + NormalizeEmail(email));

    public string HashPhone(string phone) => Hash("phone:" + NormalizePhone(phone));

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Digits only, and the last ten of them, so "+880 1977-887087" and "01977887087" match.</summary>
    public static string NormalizePhone(string phone)
    {
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    private string Hash(string value) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value)));
}
