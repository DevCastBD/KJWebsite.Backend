# Codebase Map
<!-- verified: 2026-10-13 @ 86f21c9 · tier: standard · coverage: overview-only -->

## Do not read in full
| File / path | Size | Why | Instead |
|---|---:|---|---|
| `legacy/**/*.sql` | 8–11 KB | legacy MySQL dumps: test data, but include password hashes | read `legacy/README.md` instead |
| `docs/MASTER_PLAN.md` | 57 KB | 806-line planning document | read a section below with `sed -n` |
| `openapi.v1.yaml` | generated | generated aggregate API contract | regenerate with `dotnet run --project tools/OpenApiExporter` |

## Areas
- gateway (`src/Services/ApiGateway/**`)
  purpose: YARP routes public and admin API paths to services; entry: `Program.cs`; verify: `dotnet build KJWebsite.Backend.slnx`
- content (`src/Services/ContentService/**`)
  purpose: public projects/news and admin content endpoints; in-memory data; entry: `Program.cs`; docs: `PROJECT_CONTEXT.md`
- CTA (`src/Services/CtaSubmissionService/**`)
  purpose: public CTA submission endpoint with PostgreSQL/EF Core; entry: `Program.cs`; migrations: `Migrations/`
- auth (`src/Services/AuthIdentityService/**`)
  purpose: registration and token endpoints with PostgreSQL/EF Core; entry: `Program.cs`; migrations: `Migrations/`
- shared (`src/BuildingBlocks/**`)
  purpose: Result, paging, language resolution, RFC 9457/legacy-compatible errors, and endpoint-filter base
- tests (`tests/UnitTests/**`, `tests/IntegrationTests/**`)
  purpose: xUnit unit tests for BuildingBlocks; WebApplicationFactory smoke tests run against SQLite by default (no Docker); Postgres-specific tests (migration/collation, SQLite transfer) gate on `KJ_TEST_POSTGRES` via `[RequiresPostgresFact]`
- contract and plans (`openapi.v1.yaml`, `PROJECT_CONTEXT.md`, `docs/**`)
  purpose: checked-in API contract, onboarding context, source-of-truth plans, and ratified decisions in `docs/adr/`
- OpenAPI export (`tools/OpenApiExporter/**`)
  purpose: starts isolated development services without database initialization and merges runtime OpenAPI into `openapi.v1.yaml`; verify: `dotnet run --project tools/OpenApiExporter -- --verify`
- SQLite transfer (`tools/SqliteToPostgres/**`, `docs/SQLITE_TO_POSTGRES.md`)
  purpose: one-way import of existing Auth/CTA development data into empty migrated PostgreSQL databases
- legacy (`legacy/**`)
  purpose: read-only 2019 ASP.NET Identity snapshots; mine domain fields/statuses only; never build or run; guide: `legacy/README.md`

## Entry points
- `KJWebsite.Backend.slnx` — solution containing the four services and building blocks.
- `src/Services/*/Program.cs` — service startup, endpoints, OpenAPI/Scalar and health routes.

## Cross-cutting
- OpenAPI is served from each service; Scalar is mapped by its `Program.cs`.
- Auth, CTA, and Content errors use the shared RFC 9457 factory, preserving the legacy nested `error` object.
- Content resolves `?lang=`, then `Accept-Language`, then `en`; public list responses echo the result as `lang`.
- Auth and CTA require PostgreSQL `ConnectionStrings:AuthDb` / `ConnectionStrings:CtaDb`; both call `Database.Migrate()` at startup and own named schemas.
- Gateway destinations are hard-coded localhost addresses.
- Root `.editorconfig`, `Directory.Build.props`, and `Directory.Packages.props` govern the active solution; legacy snapshots are excluded.

## Long documents
- `docs/MASTER_PLAN.md` (784 lines) — read with `sed -n`, never whole.
  §1 Current state 44–97 · §4 Engineering standards 315–369 · §5 Phase plan 370–674 · §7 Security 691–713 · §8 Operations 714–730

## Gotchas
- Current auth stores plain-text passwords, access tokens are process-local, and content admin uses `dev-admin-token`; not production-safe.
- CI: .github/workflows/ci.yml (`build`, `dependency-scan` — both required on develop); Dependabot weekly. Local .NET 10 SDK may be at ~/.dotnet10 (system SDK can be older).
- Npgsql `timestamp with time zone` requires UTC; normalize incoming offset timestamps before storing.
