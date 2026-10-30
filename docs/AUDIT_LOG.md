# Audit log (BE-033, #43; closes G14)

Every administrative mutation leaves an `audit_events` row in the service that owns the data (schema-per-service, ADR 0004): **Content** (`ContentItem`, `ContentTranslation`, `Project`, `NewsArticle`, `Tag`, `TagName`, `ContentItemTag`, `ContentRelation`, `Redirect`) and **Auth** (`AuthUser`, `AuthRole`, `IdentityRoleClaim`, `IdentityUserRole`, `MemberProfile`). The CTA service has no admin API on this branch; when one is added it must register its entities with `AddKjAuditing` and call `MapAuditLog`.

## What is recorded
`AuditInterceptor` (BuildingBlocks) turns each tracked change into a row written **in the same transaction** as the change: when (UTC), service, actor id + email (from the token), `create`/`update`/`delete`, entity, entity id, **before / after JSON**, the request (`METHOD /path`), a request id that ties together the several rows one save produces, client IP (first `X-Forwarded-For`, else the socket) and user agent.
- create: `after` is every column; delete: `before` is every column; update: only the **changed** columns, each with its previous and new value.
- Soft deletes are updates (`deletedAt`). Generated keys (identity role claims) are recorded as `new`.
- Never stored: password hashes, security/concurrency stamps (`AuditOptions.Audit<T>(ignoredProperties)`).
- Recorded only when the request carries an authenticated caller. Startup seeding, the public sign-up and the login counters are not admin mutations.
- Bulk `ExecuteUpdate`/`ExecuteDelete` bypass change tracking and would bypass the log; audited code paths use tracked changes (the rename redirect removal was changed for this reason).

## Immutable
- No route writes, updates or deletes an entry (PUT/POST/PATCH/DELETE answer 403/404/405).
- The interceptor throws if an audit row is modified or removed through EF.
- PostgreSQL: migrations add a trigger that refuses `UPDATE`, `DELETE` and `TRUNCATE` on `audit_events` (SQLSTATE 42501), verified by a PostgreSQL test. A superuser can still drop the trigger, so production should also revoke those privileges from the application role.
- Not done: hash-chaining rows for tamper evidence.

## Reading it
`GET /api/v1/admin/content/audit` and `GET /api/v1/admin/auth/audit` (+ `/{id}`), permission **`audit.view`** (its own key; `content.*` does not open it). Newest first; filters `actor` (id or email), `entity`, `entityId`, `action`, `requestId`, `from`, `to`. Paging is by cursor (`nextCursor` of the previous page, `limit` 1-100): a deep page costs the same as the first, backed by indexes on time, entity and actor. Responses are `no-store`; before/after hold personal data, so they are never copied into application logs.

## Open
- Retention (the log outlives the records it describes) waits on the data-retention decision, #110. Nothing is ever deleted meanwhile.
- No cross-service view yet; the dashboard/global search issue (BE-038) can fan out to both endpoints.
