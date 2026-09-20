# Codebase Map
<!-- generated: 2026-09-20 @ 7aac7bb · tier: standard · coverage: overview-only -->

## Do not read in full
| File / path | Size | Why | Instead |
|---|---:|---|---|
| `kj-registration*/.vs/**/storage.ide*` | 3–4 MB | tracked legacy Visual Studio database artifacts | do not inspect |
| `kj-registration*/obj/project.assets.json` | 587 KB | generated NuGet restore output | inspect the legacy `.csproj` instead |
| `docs/MASTER_PLAN.md` | 57 KB | 806-line planning document | read a section below with `sed -n` |
| `openapi.v1.yaml` | generated | generated aggregate API contract | regenerate with `dotnet run --project tools/OpenApiExporter` |

## Areas
- gateway (`src/Services/ApiGateway/**`)
  purpose: YARP routes public and admin API paths to services; entry: `Program.cs`; verify: `dotnet build KJWebsite.Backend.slnx`
- content (`src/Services/ContentService/**`)
  purpose: public projects/news and admin content endpoints; in-memory data; entry: `Program.cs`; docs: `PROJECT_CONTEXT.md`
- CTA (`src/Services/CtaSubmissionService/**`)
  purpose: public CTA submission endpoint with SQLite/EF Core; entry: `Program.cs`; migrations: `Migrations/`
- auth (`src/Services/AuthIdentityService/**`)
  purpose: registration and token endpoints with SQLite/EF Core; entry: `Program.cs`; migrations: `Migrations/`
- shared (`src/BuildingBlocks/**`)
  purpose: shared building blocks; currently a minimal placeholder project
- contract and plans (`openapi.v1.yaml`, `PROJECT_CONTEXT.md`, `docs/**`)
  purpose: checked-in API contract, onboarding context, and source-of-truth plans
- OpenAPI export (`tools/OpenApiExporter/**`)
  purpose: starts isolated development services and merges their runtime OpenAPI into `openapi.v1.yaml`; verify: `dotnet run --project tools/OpenApiExporter -- --verify`
- legacy (`kj-registration/`, `kj-registration-AwaitingUserFeatures/`)
  purpose: read-only historical ASP.NET Identity snapshots; mine domain fields/statuses only; never build or run

## Entry points
- `KJWebsite.Backend.slnx` — solution containing the four services and building blocks.
- `src/Services/*/Program.cs` — service startup, endpoints, OpenAPI/Scalar and health routes.

## Cross-cutting
- OpenAPI is served from each service; Scalar is mapped by its `Program.cs`.
- Auth and CTA use `ConnectionStrings:AuthDb` / `ConnectionStrings:CtaDb`; both call `Database.Migrate()` at startup.
- Gateway destinations are hard-coded localhost addresses.
- Root `.editorconfig`, `Directory.Build.props`, and `Directory.Packages.props` govern the active solution; legacy snapshots are excluded.

## Long documents
- `docs/MASTER_PLAN.md` (806 lines) — read with `sed -n`, never whole.
  §1 Current state 44–97 · §4 Engineering standards 338–392 · §5 Phase plan 393–695 · §7 Security 712–734 · §8 Operations 735–751

## Gotchas
- Current auth stores plain-text passwords, access tokens are process-local, and content admin uses `dev-admin-token`; not production-safe.
- Docker, CI, tests, and a configured formatter are planned but absent.
