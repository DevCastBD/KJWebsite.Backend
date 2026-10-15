<!-- phase: DONE | branch: docs/issue-12-adr-ratification | tasks: 10/10
     base: 420e2fd | updated: 2026-10-14
     next: none — awaiting commit/push decision from user -->
# Plan: Ratify remaining ADRs (BE-002, issue #12)

## Tasks
- [x] `docs/adr/0002-identity.md` — ASP.NET Core Identity
- [x] `docs/adr/0003-token-format.md` — asymmetric JWT (RS256/ES256), 15-min access, rotating refresh
- [x] `docs/adr/0005-background-jobs.md` — Hangfire + transactional outbox
- [x] `docs/adr/0006-object-storage.md` — Cloudflare R2
- [x] `docs/adr/0007-email-provider.md` — Postmark primary, SendGrid/SES as documented fallbacks behind an interface
- [x] `docs/adr/0009-validation.md` — FluentValidation + shared endpoint filter
- [x] `docs/adr/0010-mapping.md` — explicit extensions or Mapperly, no reflection mappers
- [x] `docs/adr/0012-testing.md` — xUnit + FluentAssertions + WebApplicationFactory on SQLite, optional KJ_TEST_POSTGRES (matches #152 as shipped), no Testcontainers
- [x] `docs/adr/0013-hosting.md` — shape only: manual `dotnet publish` deploys, no containers for now; full provider/budget decision stays open on #106
- [x] Posted comment on #12 noting ADR-008/011/014 remain blocked on #111/#153/#110

- [x] `0008-payments.md`, `0011-observability.md`, `0014-data-residency-and-privacy.md` — Accepted, ratified by FHShubho per user; 014 numeric windows deferred to #110

## Tests
- None — documentation only (architecture decisions, no code). Verified by review: each file follows the existing 0001/0004 format (Status/Date/Ratified by/Source, Context, Decision, Rejected alternatives, Consequences) and matches the plan's stated recommendation plus the two user-picked pins.

## Files
- `docs/adr/0002-identity.md` (new)
- `docs/adr/0003-token-format.md` (new)
- `docs/adr/0005-background-jobs.md` (new)
- `docs/adr/0006-object-storage.md` (new)
- `docs/adr/0007-email-provider.md` (new)
- `docs/adr/0009-validation.md` (new)
- `docs/adr/0010-mapping.md` (new)
- `docs/adr/0012-testing.md` (new)
- `docs/adr/0013-hosting.md` (new)
