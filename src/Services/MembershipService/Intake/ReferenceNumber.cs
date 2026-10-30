using System.Security.Cryptography;

namespace MembershipService.Intake;

/// <summary>
/// Public application references: the year plus eight random characters (about 40 bits). Random rather than
/// counted, so one reference says nothing about its neighbours and cannot be walked.
/// </summary>
public static class ReferenceNumber
{
    // Crockford base32: no I, L, O or U, so a reference read over the phone is hard to mistake.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Create(int year)
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"KJ-{year}-{chars}";
    }

    public static string Normalize(string reference) => reference.Trim().ToUpperInvariant();
}
