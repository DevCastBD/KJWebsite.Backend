# Codebase Map
<!-- verified: 2026-11-09 @ 598597b · tier: standard · coverage: overview-only -->

## Do not read in full
| File / path | Size | Why | Instead |
|---|---:|---|---|
| `legacy/**/*.sql` | 8–11 KB | legacy MySQL dumps: test data, but include password hashes | read `legacy/README.md` instead |
| `docs/MASTER_PLAN.md` | 15 KB | 206-line plan; the plan wins over any issue | read a section below with `sed -n` |
| `openapi.v1.yaml` | generated | generated aggregate API contract | regenerate with `dotnet run --project tools/OpenApiExporter` |

## Areas
- gateway (`src/Services/ApiGateway/**`)
  purpose: YARP routes public and admin API paths to services; entry: `Program.cs`; verify: `dotnet build KJWebsite.Backend.slnx`
- content (`src/Services/ContentService/**`)
  purpose: public projects/news and admin content endpoints; PostgreSQL `content` schema via `Data/ContentStore.cs`; seed JSON in `Data/Seed/`; entry: `Program.cs`; docs: `docs/CONTENT_CONTRACT.md`
- CTA (`src/Services/CtaSubmissionService/**`)
  purpose: public CTA submission endpoint with PostgreSQL/EF Core; entry: `Program.cs`; migrations: `Migrations/`
- membership (`src/Services/MembershipService/**`)
  purpose: public application intake (port 7004, schema `membership`); encrypted PII fields, HMAC duplicate check, attachments behind `IAttachmentStore`; entry: `Program.cs`, `Intake/`; docs: `docs/MEMBERSHIP_INTAKE.md`
- finance (`src/Services/FinanceService/**`)
  purpose: donations (port 7005, schema `finance`): public intake/status/presets, finance back office, append-only ledger (entities + PG triggers), `Money` lives in BuildingBlocks `Finance/`; `Payments/` holds the gateway abstraction + SSLCommerz/bKash/Nagad/Stripe/PayPal adapters; `Reconciliation/` holds the pending-payment resolver (+ timer worker) and gateway payout import/matching; `Payments/` also has the webhook processor/endpoints; `Offline/` holds offline-donation recording with maker-checker, proofs and the proof store; `Receipts/` holds gapless hash-chained receipts, the bilingual PDF (QuestPDF + embedded Bengali font) and annual statements; `Donors/` holds the donor CRM (derived history, reproducible segments, consent gate, export); `Campaigns/` holds fundraising campaigns, the cached public progress and reconciliation; `Expenses/` holds expense claims, tiered distinct-approver approval, payment and rollups; `Budgets/` holds fiscal-year close, budget lines, ledger-derived variance and threshold alerts; `Banking/` holds the bank-account registry, CSV/OFX statement parsers, assisted reconciliation (auto-match, match, post-and-match, dismiss) and the balance proof; `Reports/` holds ledger-derived statements, donation/project/donor reports, CSV and XLSX writers and stored monthly report packs with signed download links; `Controls/` holds the request-and-approve layer (adjustments, reversals, refunds, reopen) and refund reconciliation; `Ledger/` holds the double-entry ledger (LedgerService, posting rules, donation posting/confirmation); entry: `Program.cs`, `Donations/`; docs: `docs/DONATIONS.md`, `docs/PAYMENT_GATEWAYS.md`, `docs/LEDGER.md`, `docs/OFFLINE_DONATIONS.md`, `docs/RECEIPTS.md`, `docs/DONORS.md`, `docs/CAMPAIGNS.md`, `docs/EXPENSES.md`, `docs/BUDGETS.md`, `docs/BANKING.md`, `docs/REPORTS.md`, `docs/CONTROLS.md`
- auth (`src/Services/AuthIdentityService/**`)
  purpose: ASP.NET Core Identity accounts, RS256 JWT + hashed rotating refresh tokens (`Tokens/`), sessions, password reset; entry: `Program.cs`; migrations: `Migrations/`; docs: `docs/AUTH_TOKENS.md`
- shared (`src/BuildingBlocks/**`)
  purpose: Result, paging, language resolution, RFC 9457/legacy-compatible errors, and endpoint-filter base
- sponsorship (planned, epic #208; not built yet)
  purpose: students, guardian consent, photos and donor-student links in a new Sponsorship module, plus a donor role in auth, donor profiles and logins in finance and read-only `/api/v1/donors/me`; decisions and API contract: `docs/SPONSORSHIP.md` (source of truth with issue #209); first step: #210 in `src/Services/AuthIdentityService/`
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
- `KJWebsite.Backend.slnx`: solution containing the six services (gateway, auth, content, CTA, membership, finance) and building blocks.
- `src/Services/*/Program.cs` — service startup, endpoints, OpenAPI/Scalar and health routes.

## Cross-cutting
- OpenAPI is served from each service; Scalar is mapped by its `Program.cs`.
- Auth, CTA, and Content errors use the shared RFC 9457 factory, preserving the legacy nested `error` object.
- Content resolves `?lang=`, then `Accept-Language`, then `en`; public list responses echo the result as `lang`.
- Auth, Content and CTA require PostgreSQL `ConnectionStrings:AuthDb` / `CtaDb` / `ContentDb`; each calls `Database.Migrate()` at startup and own named schemas.
- Gateway destinations are hard-coded localhost addresses.
- Root `.editorconfig`, `Directory.Build.props`, and `Directory.Packages.props` govern the active solution; legacy snapshots are excluded.

## Long documents
- `docs/MASTER_PLAN.md` (206 lines): read with `sed -n`.
  §1 Where we are 17 · §2 Ground rules 34 · §3 Architecture 42 · §4 Security baseline 72 · §5 Execution order 76 (P4 donations and finance, incl. sponsorship BE-102 to BE-110, at 146) · §6 Frontend alignment 193 · §7 Risks 206

## Gotchas
- Endpoints are deny-by-default: declare `.AllowAnonymous()` or `.RequirePermission(Permissions.X)` (BuildingBlocks `Security/`); a test audits every endpoint. Permissions/roles live in `KjRoles.Seed`.
- Embedded seed files named `*.en.json`/`*.bn.json` need `WithCulture="false"` or MSBuild treats `.en`/`.bn` as a culture and builds satellite assemblies.
- Gateway rate limits per IP (`docs/RATE_LIMITING.md`); set `ForwardedHeaders:*` before going public. Not production-safe yet: no email delivery (#32), refresh token in JSON body (cookie pending).
- No CI, Dependabot or Docker at this stage (plan v2); run build/format/test/OpenAPI --verify locally. Local .NET 10 SDK may be at ~/.dotnet10 (system SDK can be older).
- Npgsql `timestamp with time zone` requires UTC; normalize incoming offset timestamps before storing.
- Never compare DateTimeOffset inside an EF LINQ predicate: SQLite cannot translate it (see RefreshTokenTests).
