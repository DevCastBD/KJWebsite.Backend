using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KJWebsite.BuildingBlocks.Idempotency;

/// <summary>Endpoint metadata: this POST honours the `Idempotency-Key` header.</summary>
public sealed class IdempotentEndpointMetadata;

public sealed class IdempotencyOptions
{
    /// <summary>How long a key remembers its response. The frontend generates one key per form session, so a day covers any retry.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long a claimed key blocks duplicates while the first request is still running.</summary>
    public TimeSpan InFlightWindow { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxBodyBytes { get; set; } = 256 * 1024;
}

public static class IdempotencyExtensions
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotency-Replayed";

    /// <summary>Registers the store: Redis when `Redis:ConnectionString` is set, otherwise process memory.</summary>
    public static IServiceCollection AddKjIdempotency(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdempotencyOptions>(o =>
        {
            if (configuration.GetValue<int?>("Idempotency:WindowHours") is { } hours and > 0)
            {
                o.Window = TimeSpan.FromHours(hours);
            }
        });
        services.AddSingleton(TimeProvider.System);
        if (configuration["Redis:ConnectionString"] is { Length: > 0 } redis)
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
            services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        }
        else
        {
            services.AddSingleton<IIdempotencyStore, MemoryIdempotencyStore>();
        }

        return services;
    }

    /// <summary>Add after routing and authentication; only endpoints marked with RequireIdempotency are affected.</summary>
    public static IApplicationBuilder UseKjIdempotency(this IApplicationBuilder app) => app.UseMiddleware<IdempotencyMiddleware>();

    public static TBuilder RequireIdempotency<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new IdempotentEndpointMetadata());
}

/// <summary>
/// Replays the original response when a POST repeats an `Idempotency-Key` (closes G27). A request without the header is processed normally.
/// Reusing a key with a different body is a 422; a repeat while the first is running is a 409. Only 2xx responses are remembered, so a failed
/// attempt can be retried with the same key.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, IIdempotencyStore store, IOptions<IdempotencyOptions> options)
{
    private readonly IdempotencyOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || context.GetEndpoint()?.Metadata.GetMetadata<IdempotentEndpointMetadata>() is null)
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers[IdempotencyExtensions.HeaderName];
        if (header.Count == 0)
        {
            await next(context);
            return;
        }

        var raw = header.ToString();
        if (header.Count > 1 || raw.Length is 0 or > 255 || raw.Any(c => c < 0x21 || c > 0x7E))
        {
            await ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Idempotency-Key must be one printable ASCII value of 1-255 characters.").ExecuteAsync(context);
            return;
        }

        context.Request.EnableBuffering();
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        if (buffer.Length > _options.MaxBodyBytes)
        {
            await ApiError.Create(context, StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", "Request body is too large.").ExecuteAsync(context);
            return;
        }

        context.Request.Body.Position = 0;
        var fingerprint = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        var key = StoreKey(context, raw);

        var (outcome, existing) = await store.BeginAsync(key, fingerprint, _options.InFlightWindow, context.RequestAborted);
        if (outcome == IdempotencyBegin.Existing)
        {
            await RespondToExistingAsync(context, existing!, fingerprint);
            return;
        }

        var original = context.Response.Body;
        await using var captured = new MemoryStream();
        context.Response.Body = captured;
        try
        {
            await next(context);
        }
        catch
        {
            context.Response.Body = original;
            await store.AbandonAsync(key, CancellationToken.None);
            throw;
        }

        context.Response.Body = original;
        if (context.Response.StatusCode is >= 200 and < 300)
        {
            var record = new IdempotencyRecord(fingerprint, context.Response.StatusCode, context.Response.ContentType, context.Response.Headers.Location, captured.ToArray());
            await store.CompleteAsync(key, record, _options.Window, CancellationToken.None);
        }
        else
        {
            await store.AbandonAsync(key, CancellationToken.None);
        }

        captured.Position = 0;
        await captured.CopyToAsync(original, context.RequestAborted);
    }

    private static async Task RespondToExistingAsync(HttpContext context, IdempotencyRecord existing, string fingerprint)
    {
        if (existing.Fingerprint != fingerprint)
        {
            await ApiError.Create(context, StatusCodes.Status422UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used with a different request body.").ExecuteAsync(context);
            return;
        }

        if (!existing.IsComplete)
        {
            context.Response.Headers.RetryAfter = "1";
            await ApiError.Create(context, StatusCodes.Status409Conflict, "IDEMPOTENCY_IN_PROGRESS", "A request with this Idempotency-Key is still being processed.").ExecuteAsync(context);
            return;
        }

        context.Response.StatusCode = existing.StatusCode;
        context.Response.ContentType = existing.ContentType;
        if (existing.Location is not null)
        {
            context.Response.Headers.Location = existing.Location;
        }

        context.Response.Headers[IdempotencyExtensions.ReplayedHeaderName] = "true";
        await context.Response.Body.WriteAsync(existing.Body, context.RequestAborted);
    }

    // Scoped to method and path so one key cannot collide across endpoints; hashed so arbitrary header text never becomes a store key.
    private static string StoreKey(HttpContext context, string header) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.Request.Method}\n{context.Request.Path}\n{header}")));
}
