# BE-033 Immutable audit log with before/after on every admin mutation (#43)
- Source: issue #43 https://github.com/DevCastBD/KJWebsite.Backend/issues/43 (milestone 3, order BE-033; closes G14)
- Type: feature
## Request
- Every admin mutation writes an audit row: actor, action, entity, before/after JSON diff, IP, user agent; queryable; immutable; read needs its own permission (audit.view)
## Decisions
- Each service keeps its own audit_events table (schema-per-service, ADR 0004); one shared interceptor in BuildingBlocks captures EF changes of registered entity types; no cross-service aggregation yet
- Audited only when an authenticated actor exists (admin mutations); startup seeding and public flows are skipped
- Modified rows store only changed properties (diff); Added = after, Deleted = before; sensitive props (password hash, stamps) never stored
- Immutability: EF guard (no update/delete of audit rows), no API write path, PostgreSQL trigger in migrations; ExecuteUpdate/Delete bypass interceptors so audited code paths use tracked changes
- Keyset pagination (cursor = id) for volume; retention left to #110
- CTA service has no admin API on this base (#34 is in the unmerged stack): out of scope, must adopt the interceptor when #34 lands
## Out of scope
- Hash chaining, aggregated cross-service view, retention
## Follow-ups
