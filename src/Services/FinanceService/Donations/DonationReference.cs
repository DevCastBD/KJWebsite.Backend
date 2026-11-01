using System.Security.Cryptography;

namespace FinanceService.Donations;

/// <summary>
/// The public handle on a donation, `KJD-` plus ten Crockford base32 characters (50 random bits). It is the only thing a donor's status page
/// holds, so it must not be guessable or sequential; the unique index settles the vanishing chance of a repeat.
/// </summary>
public static class DonationReference
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const string Prefix = "KJD-";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[bytes[i] & 31];
        }

        return Prefix + new string(chars);
    }

    public static bool LooksValid(string? reference) =>
        reference is { Length: 14 } && reference.StartsWith(Prefix, StringComparison.Ordinal) && reference[Prefix.Length..].All(c => Alphabet.Contains(c));
}
