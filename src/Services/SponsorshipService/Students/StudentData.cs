using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using SponsorshipService.Data;

namespace SponsorshipService.Students;

/// <summary>Field-level encryption for the personal columns of a student.</summary>
public interface IStudentProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}

public sealed class DataProtectionStudentProtector(IDataProtectionProvider provider) : IStudentProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("KJWebsite.Sponsorship.StudentFields.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

public static partial class StudentData
{
    public const long MaxPhotoBytes = 2 * 1024 * 1024;
    public const long MaxFormBytes = 5 * 1024 * 1024;
    private static readonly string[] Genders = ["male", "female", "other"];

    public static string NewId() => $"stu_{Guid.NewGuid():N}";

    [GeneratedRegex(@"^\+[0-9]{7,15}$")]
    private static partial Regex PhonePattern();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The names of the fields that are missing or invalid. On create every required field must be present; on edit only the fields sent are checked.</summary>
    public static List<string> Validate(StudentRequest r, bool creating, DateOnly today)
    {
        var invalid = new List<string>();

        void Required(string name, string? value, int max)
        {
            if (value is null)
            {
                if (creating)
                {
                    invalid.Add(name);
                }

                return;
            }

            if (Clean(value) is not { } text || text.Length > max)
            {
                invalid.Add(name);
            }
        }

        void Optional(string name, string? value, int max)
        {
            if (value is not null && value.Trim().Length > max)
            {
                invalid.Add(name);
            }
        }

        Required("name", r.Name, 200);
        Required("preferredName", r.PreferredName, 100);
        Required("school", r.School, 200);
        Required("class", r.Class, 50);
        Required("programme", r.Programme, 200);
        Required("district", r.District, 100);
        Required("guardianName", r.GuardianName, 200);
        Optional("address", r.Address, 500);
        Optional("storyEn", r.StoryEn, 4000);
        Optional("storyBn", r.StoryBn, 4000);

        if (r.DateOfBirth is null)
        {
            if (creating)
            {
                invalid.Add("dateOfBirth");
            }
        }
        else if (!DateOnly.TryParseExact(r.DateOfBirth, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var born)
            || born > today || born < today.AddYears(-30))
        {
            invalid.Add("dateOfBirth");
        }

        if (r.Gender is { Length: > 0 } gender && !Genders.Contains(gender))
        {
            invalid.Add("gender");
        }

        if (r.GuardianPhone is { Length: > 0 } phone && !PhonePattern().IsMatch(phone.Trim()))
        {
            invalid.Add("guardianPhone");
        }

        if (r.Status is not null && !StudentStatuses.All.Contains(r.Status))
        {
            invalid.Add("status");
        }

        return invalid;
    }

    /// <summary>Copies the fields that were sent. Optional text sent empty is cleared.</summary>
    public static void Apply(StudentEntity s, StudentRequest r, IStudentProtector protector)
    {
        if (Clean(r.Name) is { } name)
        {
            s.NameProtected = protector.Protect(name);
        }

        if (Clean(r.PreferredName) is { } preferred)
        {
            s.PreferredName = preferred;
        }

        if (r.DateOfBirth is not null)
        {
            s.DateOfBirthProtected = protector.Protect(r.DateOfBirth.Trim());
        }

        if (r.Gender is not null)
        {
            s.Gender = Clean(r.Gender);
        }

        if (Clean(r.School) is { } school)
        {
            s.School = school;
        }

        if (Clean(r.Class) is { } className)
        {
            s.ClassName = className;
        }

        if (Clean(r.Programme) is { } programme)
        {
            s.ProgrammeId = programme;
        }

        if (Clean(r.District) is { } district)
        {
            s.District = district;
        }

        if (r.Address is not null)
        {
            s.AddressProtected = Clean(r.Address) is { } address ? protector.Protect(address) : null;
        }

        if (Clean(r.GuardianName) is { } guardian)
        {
            s.GuardianNameProtected = protector.Protect(guardian);
        }

        if (r.GuardianPhone is not null)
        {
            s.GuardianPhoneProtected = Clean(r.GuardianPhone) is { } phone ? protector.Protect(phone) : null;
        }

        if (r.StoryEn is not null)
        {
            s.StoryEn = Clean(r.StoryEn);
        }

        if (r.StoryBn is not null)
        {
            s.StoryBn = Clean(r.StoryBn);
        }

        if (r.Status is not null)
        {
            s.Status = r.Status;
        }
    }

    public static string ETagOf(StudentEntity s) => $"\"{s.Version.ToString(CultureInfo.InvariantCulture)}\"";

    public static StudentSummary Summary(StudentEntity s, IStudentProtector protector, ProgrammeRef programme, bool consentGranted) =>
        new(s.Id, protector.Unprotect(s.NameProtected), s.PreferredName, s.ClassName, s.School, programme, s.District, s.Status,
            Sponsored: false, HasPhoto: s.PhotoKey is not null, ConsentGranted: consentGranted);

    public static StudentDetail Detail(StudentEntity s, IStudentProtector protector, ProgrammeRef programme, IReadOnlyList<ConsentEntity> consents)
    {
        var latest = consents.Count == 0 ? null : consents[0];
        return new StudentDetail(
            s.Id, protector.Unprotect(s.NameProtected), s.PreferredName, protector.Unprotect(s.DateOfBirthProtected), s.Gender, s.School, s.ClassName,
            programme, s.District, s.AddressProtected is null ? null : protector.Unprotect(s.AddressProtected),
            protector.Unprotect(s.GuardianNameProtected), s.GuardianPhoneProtected is null ? null : protector.Unprotect(s.GuardianPhoneProtected),
            s.StoryEn, s.StoryBn, s.Status, s.PhotoKey is not null, s.FormKey is not null, s.FormFileName,
            new StudentConsentState(latest?.Granted == true, latest?.RecordedAt, latest?.ActorId),
            [.. consents.Select(c => new StudentConsentRow(c.Id, c.Granted, c.FormKey is not null, c.RecordedAt, c.ActorId, c.Note))],
            [], s.CreatedAt, s.UpdatedAt);
    }
}

/// <summary>What a donor may see of a student: the single allow-list (D2) behind the staff preview and the donor endpoints.</summary>
public static class DonorProjection
{
    public static DonorStudentView Of(StudentEntity s, bool consentGranted, ProgrammeRef programme, string language)
    {
        string? story = null;
        string? storyLanguage = null;
        if (consentGranted)
        {
            var (first, second, firstLang, secondLang) = language == "bn" ? (s.StoryBn, s.StoryEn, "bn", "en") : (s.StoryEn, s.StoryBn, "en", "bn");
            if (!string.IsNullOrWhiteSpace(first))
            {
                (story, storyLanguage) = (first, firstLang);
            }
            else if (!string.IsNullOrWhiteSpace(second))
            {
                (story, storyLanguage) = (second, secondLang);
            }
        }

        return new DonorStudentView(s.Id, s.PreferredName, s.ClassName, s.School, programme, s.District, s.Status,
            HasPhoto: consentGranted && s.PhotoKey is not null, story, storyLanguage);
    }
}
