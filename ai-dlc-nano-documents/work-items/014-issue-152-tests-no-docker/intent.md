# Make dotnet test run with no Docker (BE-001)

- Source: issue #152 <https://github.com/DevCastBD/KJWebsite.Backend/issues/152> (tracked under epic #115, BE-001)
- Type: chore (test infrastructure)

## Request

- `dotnet test` must pass on a clean clone with no Docker and no env vars set.
- Auth/CTA integration suites currently require Testcontainers (real PostgreSQL in Docker) for every test, including plain CRUD smoke tests.
- Provider-specific behavior (migrations, schemas, ICU Bengali collation, the Npgsql `DateTimeOffset` translation regression from #11) must still be verified against real PostgreSQL, opt-in via an env var, skipping with a clear reason when unset.
- Remove the `Testcontainers.PostgreSql` package reference.
- Gitignore `*.db`, `*.db-shm`, `*.db-wal`.
- Update README "Testing" section (no `CONTRIBUTING.md` exists in this repo).

## Decisions

- Issue #152 predates #21 (PostgreSQL-only production decision: `Program.cs` hardcodes `UseNpgsql` in both Auth and CTA services, schema-per-service, ICU Bengali collation). "SQLite by default, the provider services already use" is no longer literally true.
- Resolution (user confirmed): split integration tests into two buckets instead of reinterpreting the whole issue:
  - **SQLite-by-default bucket** — `AuthSmokeTests`, `CtaSmokeTests` (plain CRUD, no provider-specific behavior). Their factories override the DI-registered `DbContextOptions` to point at a per-test-class temp-file SQLite db instead of Postgres.
  - **Postgres-required bucket** — `PostgreSqlRefreshTokenTests`, `PostgreSqlMigrationTests`, `SqliteDataTransferTests`. These read a connection string from `KJ_TEST_POSTGRES` and skip (with reason) when it is unset. No Testcontainers container is started by any test.
  - `Program.cs` startup in both services changes `db.Database.Migrate()` to branch on provider: `Migrate()` for Npgsql (unchanged prod behavior), `EnsureCreated()` for Sqlite (test-only path). This is the only production code change; business logic and the Npgsql-only decision from #21 are untouched.
  - xunit 2.9.3 has no built-in dynamic skip, so a small custom `RequiresPostgresFactAttribute : FactAttribute` sets `Skip` conditionally at discovery time when `KJ_TEST_POSTGRES` is unset.
- `ContentSmokeTests` and `GatewaySmokeTests` are unaffected (no database).

## Out of scope

- Re-adding CI (explicitly deferred per epic #115 notes: "No CI/CD or Docker at this stage").
- Changing production connection-string resolution or the Npgsql-only decision from #21.
- `CONTRIBUTING.md` (file does not exist in this repo).

## Follow-ups

- `.github/workflows/ci.yml` still exists and runs `dotnet test` with no `KJ_TEST_POSTGRES` set. After this change, the 9 Postgres-specific tests (migrations, collation, refresh-token Npgsql translation, SQLite transfer) will silently skip in CI instead of running against Testcontainers. CI needs a `KJ_TEST_POSTGRES`-pointed Postgres service (or the workflow's fate needs settling, per epic #115's "No CI/CD... at this stage" note, which contradicts the workflow file still being present).
