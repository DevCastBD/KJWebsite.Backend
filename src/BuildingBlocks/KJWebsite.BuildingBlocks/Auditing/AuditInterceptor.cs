using System.Security.Claims;
using System.Text.Json;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.BuildingBlocks.Auditing;

/// <summary>Which entity types are audited and which of their properties must never be written to the log.</summary>
public sealed class AuditOptions
{
    private readonly Dictionary<Type, HashSet<string>> _audited = [];

    public string Service { get; set; } = string.Empty;

    /// <summary>Audits changes to <typeparamref name="T"/>. Listed properties (hashes, stamps, secrets) are left out of the before/after JSON.</summary>
    public AuditOptions Audit<T>(params string[] ignoredProperties)
    {
        _audited[typeof(T)] = new HashSet<string>(ignoredProperties, StringComparer.Ordinal);
        return this;
    }

    internal bool TryGet(Type type, out HashSet<string> ignored) => _audited.TryGetValue(type, out ignored!);
}

/// <summary>
/// Turns every change to an audited entity into an <see cref="AuditEvent"/> saved in the same transaction. It records only requests that
/// carry an authenticated caller, so startup seeding and public sign-ups are not "admin mutations", and it refuses to let an audit row be
/// changed or removed. Bulk `ExecuteUpdate`/`ExecuteDelete` bypass change tracking, so audited code paths must use tracked changes.
/// </summary>
public sealed class AuditInterceptor(IHttpContextAccessor accessor, AuditOptions options, TimeProvider time) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DictionaryKeyPolicy = JsonNamingPolicy.CamelCase };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var entries = context.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        if (entries.Any(e => e.Entity is AuditEvent && e.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("The audit log is append-only: audit events cannot be changed or deleted.");
        }

        var http = accessor.HttpContext;
        var actor = http?.User.FindFirstValue(KjClaims.Subject);
        var system = AuditActor.Current;
        if (string.IsNullOrEmpty(actor) && system is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Entity is AuditEvent || !options.TryGet(entry.Metadata.ClrType, out var ignored) || Describe(entry, ignored) is not { } change)
            {
                continue;
            }

            context.Set<AuditEvent>().Add(new AuditEvent
            {
                At = time.GetUtcNow().UtcDateTime,
                Service = options.Service,
                ActorId = actor ?? system!.Id,
                ActorEmail = actor is null ? null : http?.User.FindFirstValue(KjClaims.Email),
                Action = entry.State switch { EntityState.Added => "create", EntityState.Deleted => "delete", _ => "update" },
                Entity = EntityName(entry.Metadata.ClrType),
                EntityId = change.EntityId,
                Before = change.Before,
                After = change.After,
                Operation = actor is null || http is null ? Truncate(system!.Operation, 300) : Truncate($"{http.Request.Method} {http.Request.Path}", 300),
                RequestId = http is null ? null : Truncate(http.TraceIdentifier, 64),
                Ip = http is null || actor is null ? null : Truncate(ClientIp(http), 64),
                UserAgent = http is null || actor is null ? null : Truncate(http.Request.Headers.UserAgent.ToString(), 256)
            });
        }
    }

    private static (string EntityId, string? Before, string? After)? Describe(EntityEntry entry, HashSet<string> ignored)
    {
        var properties = entry.Properties.Where(p => !ignored.Contains(p.Metadata.Name)).ToList();
        var keys = entry.Properties.Where(p => p.Metadata.IsPrimaryKey()).ToList();
        var entityId = string.Join("|", keys.Select(k => k.IsTemporary ? "new" : Convert.ToString(k.CurrentValue, System.Globalization.CultureInfo.InvariantCulture)));

        switch (entry.State)
        {
            case EntityState.Added:
                return (entityId, null, Serialize(properties.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue)));
            case EntityState.Deleted:
                return (entityId, Serialize(properties.ToDictionary(p => p.Metadata.Name, p => p.OriginalValue)), null);
            default:
                var changed = properties.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)).ToList();
                return changed.Count == 0
                    ? null
                    : (entityId, Serialize(changed.ToDictionary(p => p.Metadata.Name, p => p.OriginalValue)), Serialize(changed.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue)));
        }
    }

    private static string Serialize(Dictionary<string, object?> values) => JsonSerializer.Serialize(values, Json);

    private static string EntityName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        return name.EndsWith("Entity", StringComparison.Ordinal) && name.Length > 6 ? name[..^6] : name;
    }

    // The gateway forwards the original caller in X-Forwarded-For; a service reached directly has only the socket address.
    private static string? ClientIp(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
        return forwarded.Length > 0 ? forwarded : http.Connection.RemoteIpAddress?.ToString();
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>
/// A non-request actor for background work (the scheduled publisher): changes saved inside <see cref="Begin"/> are audited as that actor
/// instead of being skipped for lack of a caller.
/// </summary>
public sealed record AuditActor(string Id, string Operation)
{
    private static readonly AsyncLocal<AuditActor?> Ambient = new();

    public static AuditActor? Current => Ambient.Value;

    public static IDisposable Begin(string id, string operation)
    {
        var previous = Ambient.Value;
        Ambient.Value = new AuditActor(id, operation);
        return new Restore(previous);
    }

    private sealed class Restore(AuditActor? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

public static class AuditServiceExtensions
{
    /// <summary>Registers the interceptor. Add it to each DbContext with `options.AddInterceptors(sp.GetRequiredService&lt;AuditInterceptor&gt;())`.</summary>
    public static IServiceCollection AddKjAuditing(this IServiceCollection services, string service, Action<AuditOptions> configure)
    {
        var options = new AuditOptions { Service = service };
        configure(options);
        services.AddHttpContextAccessor();
        services.AddSingleton(options);
        services.AddSingleton<AuditInterceptor>();
        return services;
    }
}
