using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.BuildingBlocks.Aggregates;

/// <summary>One monthly figure a service reports to the reporting warehouse. `Period` is the first day of the month; `Dimension` is "" for a total.</summary>
public sealed record AggregatePoint(string Metric, DateOnly Period, string Dimension, decimal Value, string? Currency = null);

/// <summary>A figure the source computed straight from its authoritative records (the ledger, for money), used to check the points against.</summary>
public sealed record AggregateCheck(string Name, decimal Amount, string? Currency = null);

public sealed record AggregateFeedResponse(string Source, DateTimeOffset AsOf, IReadOnlyList<AggregatePoint> Points, IReadOnlyList<AggregateCheck> Checks);

public sealed record AggregateBatch(IReadOnlyList<AggregatePoint> Points, IReadOnlyList<AggregateCheck>? Checks = null);

public static class AggregateFeedExtensions
{
    public const string Route = "/internal/v1/aggregates";
    public const string KeyHeader = "X-Internal-Key";
    public const string KeySetting = "Internal:Key";

    /// <summary>
    /// Maps the service's aggregate feed for the reporting warehouse. It is not under any gateway route and needs the shared key (`Internal:Key`); with no key configured
    /// the feed is closed. Points are always rebuilt from the whole history, so the warehouse can replace a source's rows in one step.
    /// </summary>
    public static void MapAggregateFeed(this IEndpointRouteBuilder app, string source, Func<HttpContext, Task<AggregateBatch>> collect) =>
        app.MapGet(Route, async (HttpContext context, TimeProvider clock) =>
        {
            var expected = context.RequestServices.GetRequiredService<IConfiguration>()[KeySetting];
            if (string.IsNullOrEmpty(expected) || !KeyMatches(expected, context.Request.Headers[KeyHeader].ToString()))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var asOf = clock.GetUtcNow();
            var collected = await collect(context);
            return Results.Ok(new AggregateFeedResponse(source, asOf, collected.Points, collected.Checks ?? []));
        })
        .WithName($"{source}AggregateFeed")
        .ExcludeFromDescription()
        .AllowAnonymous();

    public static bool KeyMatches(string expected, string supplied) =>
        supplied.Length > 0 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));

    public static DateOnly MonthOf(DateOnly day) => new(day.Year, day.Month, 1);

    public static DateOnly MonthOf(DateTimeOffset moment) => new(moment.Year, moment.Month, 1);

    public static string Fmt(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
