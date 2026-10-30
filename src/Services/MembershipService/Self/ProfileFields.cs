using MembershipService.Data.Entities;
using MembershipService.Intake;

namespace MembershipService.Self;

/// <summary>
/// The fields a member can change from the portal. Direct ones apply at once; sensitive ones affect records, cards and the
/// directory, so they wait for a reviewer.
/// </summary>
public static class ProfileFields
{
    public static readonly string[] Direct = ["phone", "address", "expertise"];
    public static readonly string[] Sensitive = ["firstName", "lastName", "nameBn", "dateOfBirth", "bloodGroup"];

    public static bool IsSensitive(string field) => Sensitive.Contains(field);

    public static bool IsKnown(string field) => Direct.Contains(field) || IsSensitive(field);

    /// <summary>Null when the value is acceptable for the field, otherwise false: the caller reports the field name only.</summary>
    public static bool IsValid(string field, string value, DateOnly today) => field switch
    {
        "phone" => ContactHasher.NormalizePhone(value).Length >= 7 && value.Length <= 32,
        "address" => value.Length is > 0 and <= 500,
        "expertise" => value.Length <= 500,
        "firstName" or "lastName" => value.Length is > 0 and <= 100,
        "nameBn" => value.Length <= 200,
        "dateOfBirth" => DateOnly.TryParseExact(value, "yyyy-MM-dd", out var dob) && dob <= today && dob.Year >= 1900,
        "bloodGroup" => ApplicationValidator.BloodGroups.Contains(value),
        _ => false
    };

    public static string Normalize(string field, string value) => field == "bloodGroup" ? value.Trim().ToUpperInvariant() : value.Trim();

    public static string Current(MemberEntity member, IFieldProtector protector, string field) => field switch
    {
        "phone" => Open(protector, member.PhoneProtected),
        "address" => Open(protector, member.AddressProtected),
        "dateOfBirth" => Open(protector, member.DateOfBirthProtected),
        "bloodGroup" => Open(protector, member.BloodGroupProtected),
        "expertise" => member.Expertise ?? string.Empty,
        "firstName" => member.FirstName,
        "lastName" => member.LastName,
        "nameBn" => member.NameBn ?? string.Empty,
        _ => string.Empty
    };

    public static void Apply(MemberEntity member, IFieldProtector protector, string field, string value)
    {
        switch (field)
        {
            case "phone": member.PhoneProtected = protector.Protect(value); break;
            case "address": member.AddressProtected = protector.Protect(value); break;
            case "dateOfBirth": member.DateOfBirthProtected = protector.Protect(value); break;
            case "bloodGroup": member.BloodGroupProtected = protector.Protect(value); break;
            case "expertise": member.Expertise = value; break;
            case "firstName": member.FirstName = value; break;
            case "lastName": member.LastName = value; break;
            case "nameBn": member.NameBn = value; break;
        }
    }

    private static string Open(IFieldProtector protector, string? protectedValue) => protectedValue is null ? string.Empty : protector.Unprotect(protectedValue);
}
