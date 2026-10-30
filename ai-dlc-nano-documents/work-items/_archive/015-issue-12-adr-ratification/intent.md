# Ratify the remaining architecture decisions (BE-002)

- Source: issue #12 https://github.com/DevCastBD/KJWebsite.Backend/issues/12 (epic #115, P0)
- Type: feature (documentation deliverable — committing `docs/adr/NNNN-*.md`)

## Request

- Ratify 14 ADRs total; commit each to `docs/adr/NNNN-title.md` with status, context, decision, consequences, rejected alternatives, and who ratified it + when.
- ADR-001 (microservices) and ADR-004 (PostgreSQL per service) are already ratified.
- 3 of the remaining 10 depend on open, unresolved business decisions from other issues: ADR-008 (payments, #111), ADR-014 (data retention, #110), ADR-013 (hosting/budget specifics, #106) — only the *shape* of ADR-013 (no containers, manual `dotnet publish`) is already settled by the plan v2 revision note.
- ADR-011 (observability) is explicitly deferred to #153 per the revision note — not ratified here.

## Decisions

- Content approach → use the plan's stated recommendation for each ADR, with two explicit pins: ADR-005 (background jobs) = **Hangfire** (not Quartz.NET); ADR-006 (object storage) = **Cloudflare R2** (not generic S3-compatible).
- Ratifier → Md Tahmidul Abedin (@tahmidsparrow), dated 2026-10-14.
- ADR-008 (payments, blocked on #111), ADR-014 (data retention, blocked on #110), ADR-011 (observability, deferred to #153) → left un-ratified; note as blockers in the #12 comment at WRAP-UP.
- ADR-001 confirmed already correct (true microservices) — no change needed.

## Out of scope

- ADR-008, ADR-014: blocked on #111 and #110 (open, unresolved core-member decisions). Not ratified here.
- ADR-011: deferred to #153 per the plan v2 revision note.
- Resolving #106/#110/#111 themselves.

## Follow-ups

(none yet)
