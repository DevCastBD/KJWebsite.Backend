namespace CtaSubmissionService.Contracts;

public sealed record CtaSubmissionRequest(
    string FormName,
    string CtaType,
    string Language,
    string SourcePath,
    Dictionary<string, object?> Values,
    DateTimeOffset SubmittedAt);

public sealed record CtaSubmissionRecord(
    string Id,
    string FormName,
    string CtaType,
    string Language,
    string SourcePath,
    Dictionary<string, object?> Values,
    DateTimeOffset SubmittedAt,
    DateTimeOffset CreatedAt,
    LegacyAwaitingUserProfile? AwaitingUserProfile);

// Legacy-aligned volunteer intake fields extracted from legacy/kj-registration AwaitingUser/AwaitingUserApiResource.
public sealed record LegacyAwaitingUserProfile(
    string? FirstName,
    string? LastName,
    string? Gender,
    string? ReasonForJoining,
    string? PresentOrganization,
    string? VolunteeingExperience,
    string? DateOfBirth,
    string? CityOfResidence,
    string? CountryOfResidence);

public static class LegacyAwaitingUserMapper
{
    public static LegacyAwaitingUserProfile? TryMap(Dictionary<string, object?> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var profile = new LegacyAwaitingUserProfile(
            Read(values, "firstName"),
            Read(values, "lastName"),
            Read(values, "gender"),
            Read(values, "reasonForJoining"),
            Read(values, "presentOrganization"),
            Read(values, "volunteeingExperience"),
            Read(values, "dateOfBirth"),
            Read(values, "cityOfResidence"),
            Read(values, "countryOfResidence"));

        return string.IsNullOrWhiteSpace(profile.FirstName) && string.IsNullOrWhiteSpace(profile.LastName)
            ? null
            : profile;
    }

    private static string? Read(Dictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw.ToString()?.Trim();
    }
}
