using System.Net.Mail;
using MembershipService.Contracts;

namespace MembershipService.Intake;

public static class ApplicationValidator
{
    public static readonly HashSet<string> BloodGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"
    };

    public static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase) { "bn", "en" };

    /// <summary>Names of the invalid fields only; a value is never echoed back or logged.</summary>
    public static List<string> Validate(ApplicationRequest r, DateOnly today)
    {
        var invalid = new List<string>();

        Required(invalid, "firstName", r.FirstName, 100);
        Required(invalid, "lastName", r.LastName, 100);
        Required(invalid, "currentAddress", r.CurrentAddress, 500);
        Required(invalid, "academicInstitute", r.AcademicInstitute, 200);
        Required(invalid, "occupation", r.Occupation, 100);

        if (!IsEmail(r.Email))
        {
            invalid.Add("email");
        }

        if (r.Phone is null || ContactHasher.NormalizePhone(r.Phone) is not { Length: >= 7 } || r.Phone.Length > 32)
        {
            invalid.Add("phone");
        }

        if (r.BloodGroup is null || !BloodGroups.Contains(r.BloodGroup.Trim()))
        {
            invalid.Add("bloodGroup");
        }

        if (r.Language is null || !Languages.Contains(r.Language))
        {
            invalid.Add("language");
        }

        Optional(invalid, "reasonForJoining", r.ReasonForJoining, 2000);
        Optional(invalid, "volunteeringExperience", r.VolunteeringExperience, 2000);
        Optional(invalid, "presentOrganization", r.PresentOrganization, 200);
        Optional(invalid, "heardAboutUs", r.HeardAboutUs, 200);
        Optional(invalid, "gender", r.Gender, 32);
        Optional(invalid, "cityOfResidence", r.CityOfResidence, 100);
        Optional(invalid, "countryOfResidence", r.CountryOfResidence, 100);

        if (!string.IsNullOrWhiteSpace(r.DateOfBirth)
            && (!DateOnly.TryParseExact(r.DateOfBirth.Trim(), "yyyy-MM-dd", out var dob) || dob > today || dob.Year < 1900))
        {
            invalid.Add("dateOfBirth");
        }

        return invalid;
    }

    public static bool IsEmail(string? value) =>
        value is { Length: <= 254 } && MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim() && address.Host.Contains('.');

    private static void Required(List<string> invalid, string name, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max)
        {
            invalid.Add(name);
        }
    }

    private static void Optional(List<string> invalid, string name, string? value, int max)
    {
        if (value is not null && value.Trim().Length > max)
        {
            invalid.Add(name);
        }
    }
}
