# ADR-012: xUnit + FluentAssertions + WebApplicationFactory, SQLite by default, no Testcontainers

- Status: Accepted
- Date: 2026-10-14
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12), [issue #152](https://github.com/DevCastBD/KJWebsite.Backend/issues/152)

## Context

The original plan recommended xUnit + FluentAssertions + Testcontainers (real PostgreSQL) + WebApplicationFactory. That requires Docker running on every machine and in CI. Plan v2 (2026-10-12) ruled out Docker and CI/CD at this stage, which Testcontainers directly contradicts: `dotnet test` could not pass on a clean clone with no Docker. Issue #152 (BE-001) already implemented and shipped the resolution below before this ADR was written.

## Decision

Use xUnit, FluentAssertions, and `WebApplicationFactory` for unit and integration tests. Plain CRUD smoke tests (Auth, CTA) run against a throwaway SQLite database per test class by default — no Docker, no setup. Tests that verify genuinely PostgreSQL-specific behavior (schema/migration shape, ICU collation, the Npgsql `DateTimeOffset` translation regression from #11, the SQLite-to-Postgres data transfer) opt in via a `KJ_TEST_POSTGRES` connection string pointed at a locally installed PostgreSQL, and skip with a clear reason when it is unset. No Testcontainers package is referenced anywhere in the solution.

## Rejected alternatives

- Testcontainers-backed PostgreSQL for every integration test (the original plan recommendation): rejected because it requires Docker on every contributor's machine and in CI, which plan v2 explicitly ruled out for this stage.
- SQLite for every integration test, including Postgres-specific behavior: rejected because SQLite cannot express ICU collation or reproduce provider-specific EF Core translation bugs; those tests would either not exist or would pass against a database that does not match production.

## Consequences

- `dotnet test` passes on a clean clone with no Docker and no environment variables.
- Full coverage (including the Postgres-gated tests) requires a contributor to set `KJ_TEST_POSTGRES` against a local PostgreSQL install.
- CI does not currently set `KJ_TEST_POSTGRES`, so the Postgres-gated tests skip there until that is wired in (tracked in `ai-dlc-nano-documents/backlog.md`).
