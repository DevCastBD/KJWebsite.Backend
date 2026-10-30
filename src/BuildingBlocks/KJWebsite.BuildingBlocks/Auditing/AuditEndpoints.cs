using System.Globalization;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace KJWebsite.BuildingBlocks.Auditing;

public sealed record AuditEventView(
    long Id, DateTimeOffset At, string Service, string ActorId, string? ActorEmail, string Action, string Entity, string EntityId,
    System.Text.Json.JsonElement? Before, System.Text.Json.JsonElement? After, string? Operation, string? RequestId, string? Ip, string? UserAgent);

public static class AuditEndpoints
{
    public const int MaxPageSize = 100;

    /// <summary>
    /// Read-only access to a service's audit log, newest first, behind `audit.view`. There is deliberately no write, update or delete
    /// route. Paging is by cursor (the last id seen) so a deep page costs the same as the first, however large the log is.
    /// </summary>
    public static void MapAuditLog<TContext>(this IEndpointRouteBuilder app, string route, string name) where TContext : DbContext
    {
        app.MapGet(route, async (HttpContext http, TContext db, string? actor, string? entity, string? entityId, string? action, string? requestId, DateTimeOffset? from, DateTimeOffset? to, long? cursor, int? limit) =>
        {
            var size = limit.GetValueOrDefault(50);
            if (size is < 1 or > MaxPageSize)
            {
                return ApiError.Create(http, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"limit must be 1-{MaxPageSize}.");
            }

            var query = db.Set<AuditEvent>().AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(actor))
            {
                query = query.Where(e => e.ActorId == actor || e.ActorEmail == actor);
            }

            if (!string.IsNullOrWhiteSpace(entity))
            {
                query = query.Where(e => e.Entity == entity);
            }

            if (!string.IsNullOrWhiteSpace(entityId))
            {
                query = query.Where(e => e.EntityId == entityId);
            }

            if (!string.IsNullOrWhiteSpace(action))
            {
                query = query.Where(e => e.Action == action);
            }

            if (!string.IsNullOrWhiteSpace(requestId))
            {
                query = query.Where(e => e.RequestId == requestId);
            }

            if (from is { } start)
            {
                var utc = start.UtcDateTime;
                query = query.Where(e => e.At >= utc);
            }

            if (to is { } end)
            {
                var utc = end.UtcDateTime;
                query = query.Where(e => e.At <= utc);
            }

            if (cursor is { } last)
            {
                query = query.Where(e => e.Id < last);
            }

            var page = await query.OrderByDescending(e => e.Id).Take(size + 1).ToListAsync();
            var hasMore = page.Count > size;
            var items = page.Take(size).Select(ToView).ToList();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new PagingEnvelope<AuditEventView>(items, null, hasMore ? items[^1].Id.ToString(CultureInfo.InvariantCulture) : null, size, null));
        })
        .WithName($"List{name}AuditLog")
        .WithSummary($"Query the {name} audit log")
        .WithDescription("Newest first. Filters: `actor` (id or email), `entity`, `entityId`, `action` (create/update/delete), `requestId`, `from`, `to`. Page with `cursor` = the `nextCursor` of the previous page. Requires `audit.view`; before/after hold personal data.")
        .WithTags("Admin — Audit log")
        .RequirePermission(Permissions.AuditView);

        app.MapGet($"{route}/{{id:long}}", async (HttpContext http, TContext db, long id) =>
        {
            var row = await db.Set<AuditEvent>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
            http.Response.Headers.CacheControl = "no-store";
            return row is null ? ApiError.Create(http, StatusCodes.Status404NotFound, "NOT_FOUND", "Audit event not found.") : Results.Ok(ToView(row));
        })
        .WithName($"Get{name}AuditEvent")
        .WithSummary($"Get one {name} audit event")
        .WithTags("Admin — Audit log")
        .RequirePermission(Permissions.AuditView);
    }

    private static AuditEventView ToView(AuditEvent e) => new(
        e.Id, new DateTimeOffset(DateTime.SpecifyKind(e.At, DateTimeKind.Utc)), e.Service, e.ActorId, e.ActorEmail, e.Action, e.Entity, e.EntityId,
        Parse(e.Before), Parse(e.After), e.Operation, e.RequestId, e.Ip, e.UserAgent);

    private static System.Text.Json.JsonElement? Parse(string? json) => json is null ? null : System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
}

public static class AuditPostgres
{
    /// <summary>
    /// For a migration's Up: makes `audit_events` refuse UPDATE, DELETE and TRUNCATE in the database itself, so the guarantee holds against
    /// any client, not just this code base. A PostgreSQL superuser can still drop the trigger; production should also revoke those
    /// privileges from the application role. No effect on other providers.
    /// </summary>
    public static void AddImmutability(this Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migration, string schema)
    {
        if (migration.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) != true)
        {
            return;
        }

        migration.Sql($$"""
            CREATE FUNCTION "{{schema}}".audit_events_refuse_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                RAISE EXCEPTION 'audit_events is append-only: % is not allowed', TG_OP USING ERRCODE = '42501';
            END;
            $fn$;
            CREATE TRIGGER audit_events_no_update_delete BEFORE UPDATE OR DELETE ON "{{schema}}".audit_events
                FOR EACH ROW EXECUTE FUNCTION "{{schema}}".audit_events_refuse_change();
            CREATE TRIGGER audit_events_no_truncate BEFORE TRUNCATE ON "{{schema}}".audit_events
                FOR EACH STATEMENT EXECUTE FUNCTION "{{schema}}".audit_events_refuse_change();
            """);
    }

    /// <summary>
    /// For a migration's Up, after <see cref="AddImmutability"/>: lets an UPDATE through only when every column except the three that
    /// identify the actor (`ActorEmail`, `Ip`, `UserAgent`) is unchanged and those three are being set to NULL. This is how an erasure
    /// removes a person from the audit log without being able to rewrite what happened (ADR-014). No effect on other providers.
    /// </summary>
    public static void AllowActorRedaction(this Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migration, string schema)
    {
        if (migration.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) != true)
        {
            return;
        }

        migration.Sql($$"""
            CREATE OR REPLACE FUNCTION "{{schema}}".audit_events_refuse_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                IF TG_OP = 'UPDATE' THEN
                    IF (to_jsonb(NEW) - 'ActorEmail' - 'Ip' - 'UserAgent') = (to_jsonb(OLD) - 'ActorEmail' - 'Ip' - 'UserAgent')
                       AND NEW."ActorEmail" IS NULL AND NEW."Ip" IS NULL AND NEW."UserAgent" IS NULL THEN
                        RETURN NEW;
                    END IF;
                END IF;
                RAISE EXCEPTION 'audit_events is append-only: % is not allowed', TG_OP USING ERRCODE = '42501';
            END;
            $fn$;
            """);
    }

    public static void RemoveImmutability(this Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migration, string schema)
    {
        if (migration.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) != true)
        {
            return;
        }

        migration.Sql($$"""
            DROP TRIGGER audit_events_no_truncate ON "{{schema}}".audit_events;
            DROP TRIGGER audit_events_no_update_delete ON "{{schema}}".audit_events;
            DROP FUNCTION "{{schema}}".audit_events_refuse_change();
            """);
    }
}
