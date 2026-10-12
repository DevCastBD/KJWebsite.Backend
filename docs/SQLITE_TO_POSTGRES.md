# Development data cutover: SQLite to PostgreSQL 17

Auth and CTA own separate PostgreSQL databases and named schemas. This procedure preserves the SQLite files and imports into empty PostgreSQL tables. ContentService still keeps its data in memory. The seeded `admin@site.org` account is development-only and still has an unsafe plain-text password until the identity work item replaces it.

## 1. Back up and provision

Stop the Auth and CTA services so the source files stop changing. Back up both SQLite databases with the SQLite CLI's online backup command (use your actual file paths):

```bash
sqlite3 auth.db ".backup 'auth.backup.db'"
sqlite3 cta.db ".backup 'cta.backup.db'"
```

Keep the originals and backups until the new services have passed validation. Do not copy a live SQLite database file without its WAL state. Compose now uses a new `postgres-v17-migrated-data` volume, leaving the previous `postgres-data` volume intact. Start only PostgreSQL while importing:

```bash
docker compose up -d postgres
```

Compose exposes PostgreSQL on host port **5433** to avoid the usual local PostgreSQL port 5432. Its `auth` and `cta` databases are created on a fresh volume. For an external PostgreSQL server, create those databases first and use its own connection strings. Use a fresh target: the transfer tool refuses non-empty tables.

## 2. Apply the PostgreSQL baselines

Set `ConnectionStrings__AuthDb` and `ConnectionStrings__CtaDb` in the shell running `dotnet ef`. These examples use the development-only Compose credentials; replace them outside Compose.

```bash
export ConnectionStrings__AuthDb='Host=localhost;Port=5433;Database=auth;Username=postgres;Password=postgres'
export ConnectionStrings__CtaDb='Host=localhost;Port=5433;Database=cta;Username=postgres;Password=postgres'
dotnet ef database update --project src/Services/AuthIdentityService --startup-project src/Services/AuthIdentityService
dotnet ef database update --project src/Services/CtaSubmissionService --startup-project src/Services/CtaSubmissionService
```

In PowerShell, use `$env:ConnectionStrings__AuthDb = '...'` and `$env:ConnectionStrings__CtaDb = '...'` instead of `export`. For review or a pre-deploy step, generate idempotent SQL with `dotnet ef migrations script --idempotent --project <service-path> --startup-project <service-path> --output <script-path>`. Run each script against only its service database. These baselines create `auth` and `cta` schemas, their tables, and a migration-history table within each schema. CTA also creates a Bengali ICU collation. PostgreSQL must have ICU support.

## 3. Import the backed-up data

Run the importer **before** starting Auth or CTA. Starting in Development seeds rows, and the importer deliberately refuses a non-empty target. The connection string goes in an environment variable to keep it out of the process command line.

```bash
export KJ_MIGRATION_PG="$ConnectionStrings__AuthDb"
dotnet run --project tools/SqliteToPostgres -- auth auth.backup.db
export KJ_MIGRATION_PG="$ConnectionStrings__CtaDb"
dotnet run --project tools/SqliteToPostgres -- cta cta.backup.db
```

In PowerShell, set `$env:KJ_MIGRATION_PG = $env:ConnectionStrings__AuthDb` (and later `CtaDb`). The importer copies `users` and `refresh_tokens` for Auth and `cta_submissions` for CTA in one transaction per database. It checks the target column set, requires empty tables, preserves Bengali text, normalizes timestamps to UTC, and verifies row counts before committing. No SQLite row is changed. If it fails, correct the cause and retry against a fresh empty target.

## 4. Verify and cut over

Compare the importer's counts with `SELECT COUNT(*)` from each SQLite source table. In PostgreSQL, inspect `auth.users`, `auth.refresh_tokens`, and `cta.cta_submissions`, plus each schema's `__EFMigrationsHistory`. Check a sample Bengali `FirstName` and `ValuesJson` value. Then start the services with `docker compose up --build` or local `dotnet run` using the PostgreSQL connection strings. Exercise registration/login, token refresh, and a CTA submission through the gateway.

For rollback **before accepting new PostgreSQL writes**, stop the new services and run the previous SQLite-backed release against the untouched original files. Keep the previous Compose volume as a separate backup. If PostgreSQL has accepted new writes, reconcile them before reverting; switching the old application back without reconciliation would lose those writes. Later PostgreSQL schema changes must use expand → migrate → contract over at least one release; do not drop fields still used by the previous release.

An existing Compose volume created with `EnsureCreated` has tables without EF migration history. Do not run the new baselines against those tables. Preserve that volume, export its data, and import into a fresh migrated volume after reviewing its schema and data; the SQLite importer is only for SQLite sources.
