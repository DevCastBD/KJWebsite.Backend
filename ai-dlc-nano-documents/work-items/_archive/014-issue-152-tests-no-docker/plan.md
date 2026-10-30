<!-- phase: DONE | branch: feat/issue-152-tests-no-docker | tasks: 9/9
     base: 2052a03 | updated: 2026-10-13
     next: none — awaiting commit/push decision from user -->
# Plan: Make dotnet test run with no Docker (BE-001, issue #152)

## Tasks
- [x] Add `RequiresPostgresFactAttribute : FactAttribute` (sets `Skip` at discovery time when `KJ_TEST_POSTGRES` env var is unset)
- [x] `TestInfrastructure.cs`: add SQLite-backed `AuthApiFactory`/`CtaApiFactory` (temp-file db per instance, override `DbContextOptions<TContext>` registration); rename current Postgres-env-var factories to `PostgresAuthApiFactory`/`PostgresCtaApiFactory` (connection string from `KJ_TEST_POSTGRES`, no Testcontainers)
- [x] `AuthIdentityService/Program.cs` and `CtaSubmissionService/Program.cs`: branch startup DB init on `db.Database.IsNpgsql()` — `Migrate()` for Npgsql, `EnsureCreated()` for Sqlite. Also made `CtaDbContext`'s ICU Bengali collation conditional on `IsNpgsql()` (Sqlite's DDL can't parse `bn-x-icu`, discovered during CONSTRUCT)
- [x] `AuthSmokeTests.cs`, `CtaSmokeTests.cs`: drop `PostgreSqlFixtures` collection/constructor dependency, construct the new SQLite factory directly
- [x] `RefreshTokenTests.cs` (`PostgreSqlRefreshTokenTests`), `PostgreSqlMigrationTests.cs`, `SqliteDataTransferTests.cs`: use `[RequiresPostgresFact]` in place of `[Fact]`, get the Postgres connection from `KJ_TEST_POSTGRES` instead of Testcontainers. Reused `auth_test`/`cta_test` databases for the first two; unique throwaway databases (created and dropped) for the transfer test, which needs a guaranteed-empty target
- [x] Remove `Testcontainers.PostgreSql` from `Directory.Packages.props` and `KJWebsite.IntegrationTests.csproj`; added `Microsoft.EntityFrameworkCore.Sqlite`
- [x] `.gitignore`: add `*.db`, `*.db-shm`, `*.db-wal`
- [x] README "Testing" section: document the two modes (SQLite default, `KJ_TEST_POSTGRES` opt-in); noted CI doesn't set `KJ_TEST_POSTGRES` yet (follow-up)
- [x] Ran `dotnet test` with no env vars (20 passed, 9 skipped with reason) and again against a real PostgreSQL 17 (29 passed, 0 skipped), twice to confirm idempotency; no Testcontainers, no Docker required for the default path

## Tests
Standard tier. Existing suite is the regression check; verified in both modes (see above). `dotnet build` and a full `dotnet test` pass. `dotnet format --verify-no-changes` fails, but repo-wide and pre-existing (local `core.autocrlf=true` + no `.gitattributes`, flags files never touched by this item, e.g. `ApiError.cs`) — not a regression, out of scope to fix here.

## Files
- `tests/IntegrationTests/KJWebsite.IntegrationTests/TestInfrastructure.cs`
- `tests/IntegrationTests/KJWebsite.IntegrationTests/RequiresPostgresFactAttribute.cs` (new)
- `tests/IntegrationTests/KJWebsite.IntegrationTests/AuthSmokeTests.cs`
- `tests/IntegrationTests/KJWebsite.IntegrationTests/CtaSmokeTests.cs`
- `tests/IntegrationTests/KJWebsite.IntegrationTests/RefreshTokenTests.cs`
- `tests/IntegrationTests/KJWebsite.IntegrationTests/PostgreSqlMigrationTests.cs`
- `tests/IntegrationTests/KJWebsite.IntegrationTests/SqliteDataTransferTests.cs`
- `tests/IntegrationTests/KJWebsite.IntegrationTests/KJWebsite.IntegrationTests.csproj`
- `src/Services/AuthIdentityService/Program.cs`
- `src/Services/CtaSubmissionService/Program.cs`
- `src/Services/CtaSubmissionService/Data/CtaDbContext.cs`
- `Directory.Packages.props`
- `.gitignore`
- `README.md`
