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
  purpose: public projects/news and admin content endpoints; PostgreSQL `content` schema via `Data/ContentStore.cs`; seed JSON in `Data/Seed/`; entry: `Program.cs`; docs: `docs/CONTENT_CONTRACT.md`
- CTA (`src/Services/CtaSubmissionService/**`)
  purpose: public CTA submission endpoint with honeypot/timing/Turnstile checks and admin list/triage/CSV export (`docs/CTA_ADMIN.md`), PostgreSQL/EF Core, transactional outbox + Hangfire (`docs/OUTBOX.md`), bilingual email via outbox (`docs/EMAIL.md`); entry: `Program.cs`; migrations: `Migrations/`
- auth (`src/Services/AuthIdentityService/**`)
  purpose: ASP.NET Core Identity accounts, RS256 JWT + hashed rotating refresh tokens (`Tokens/`), sessions, password reset; entry: `Program.cs`; migrations: `Migrations/`; docs: `docs/AUTH_TOKENS.md`
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
- OpenAPI is served from each service; Scalar is mapped by its `Program.cs`. Logging, health (`/health/live|ready|startup`) and `/metrics` come from `AddKjObservability` (`docs/OBSERVABILITY.md`); PII redaction is automatic, extend `SensitiveNames` for new personal fields.
- Auth, CTA, and Content errors use the shared RFC 9457 factory, preserving the legacy nested `error` object. Bodies and list queries validate with FluentValidation (`docs/VALIDATION.md`); new endpoints add a validator + `.WithValidation<T>()`.
- Content resolves `?lang=`, then `Accept-Language`, then `en`; public list responses echo the result as `lang`.
- Auth, Content and CTA require PostgreSQL `ConnectionStrings:AuthDb` / `CtaDb` / `ContentDb`; each calls `Database.Migrate()` at startup and own named schemas.
- Gateway routes/clusters/destinations come from `ReverseProxy` config per environment (`docs/GATEWAY.md`); correlation ID middleware is in BuildingBlocks (services wire it in #31).
- Root `.editorconfig`, `Directory.Build.props`, and `Directory.Packages.props` govern the active solution; legacy snapshots are excluded.

## Long documents
- `docs/MASTER_PLAN.md` (784 lines) — read with `sed -n`, never whole.
  §1 Current state 44–97 · §4 Engineering standards 315–369 · §5 Phase plan 370–674 · §7 Security 691–713 · §8 Operations 714–730

## Gotchas
- Secrets: user-secrets locally, env/vault deployed (`docs/SECRETS.md`); `tools/scan-secrets.sh` before releases; a test rejects secrets in settings files.
- Endpoints are deny-by-default: declare `.AllowAnonymous()` or `.RequirePermission(Permissions.X)` (BuildingBlocks `Security/`); a test audits every endpoint. Permissions/roles live in `KjRoles.Seed`.
- Embedded seed files named `*.en.json`/`*.bn.json` need `WithCulture="false"` or MSBuild treats `.en`/`.bn` as a culture and builds satellite assemblies.
- Gateway rate limits per IP (`docs/RATE_LIMITING.md`); set `ForwardedHeaders:*` before going public. Not production-safe yet: no email delivery (#32), refresh token in JSON body (cookie pending).
- No CI, Dependabot or Docker at this stage (plan v2); run build/format/test/OpenAPI --verify locally. Local .NET 10 SDK may be at ~/.dotnet10 (system SDK can be older).
- Npgsql `timestamp with time zone` requires UTC; normalize incoming offset timestamps before storing.
- Never compare DateTimeOffset inside an EF LINQ predicate: SQLite cannot translate it (see RefreshTokenTests).
