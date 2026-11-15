namespace SponsorshipService.Links;

/// <summary>
/// "Today" for the foundation, not for the server: link start and end dates are calendar days entered by staff in Bangladesh, so the day rolls
/// over at local midnight (setting `Sponsorship:TimeZone`, default Asia/Dhaka) and not at 06:00 local time as it would in UTC.
/// </summary>
public sealed class FoundationDay(TimeProvider clock, IConfiguration configuration)
{
    public const string DefaultZone = "Asia/Dhaka";

    private readonly TimeZoneInfo _zone = Resolve(configuration["Sponsorship:TimeZone"] ?? DefaultZone);

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), _zone).DateTime);

    private static TimeZoneInfo Resolve(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"Sponsorship:TimeZone '{id}' is not a known time zone id.", ex);
        }
    }
}
