<!-- phase: CONSTRUCT | branch: feature/issue-21-postgresql | tasks: 1/8
     base: 4a38da2 | updated: 2026-10-13
     next: validate PostgreSQL baselines, then complete data transfer and tests -->
# Plan: PostgreSQL 17 migration
## Tasks
- [x] Record ADR-004 with ratifier, rejected options, and service data ownership; align the master plan where needed.
- [ ] Set Npgsql as the only runtime EF provider for Auth and CTA; require PostgreSQL connection strings and remove `EnsureCreated` paths.
- [ ] Map Auth and CTA tables and migration history to named `auth` and `cta` schemas in their separate databases.
- [ ] Remove the six tracked SQLite migration files, generate PostgreSQL baselines, and validate applying each to an empty database.
- [ ] Update Compose, local configuration, and development seed data for migration-based startup, including Bengali text and collation.
- [ ] Provide repeatable `dotnet ef` commands and a documented SQLite-to-PostgreSQL export/import path with verification and rollback steps.
- [ ] Update integration coverage to use migrations on real PostgreSQL, checking schemas, seed data, Bengali ordering, and API behavior.
- [ ] Run the full solution tests, format verification, and an end-to-end service smoke check.
## Tests
- High blast radius: clean PostgreSQL 17 database migration, seed and Bengali collation checks, SQLite export/import fixture, API smoke tests, full `dotnet test KJWebsite.Backend.slnx`, `dotnet format ... --verify-no-changes --no-restore`.
- Docker Desktop is currently unavailable; use the running local PostgreSQL 17.10 for database verification if Docker remains unavailable, and report Testcontainers limitations.
## Files
- `docs/adr/0004-postgresql.md`, `docs/MASTER_PLAN.md`, `README.md`, database migration guide/scripts.
- Auth and CTA `Program.cs`, `DbContext`, migrations, package references, configuration; `Directory.Packages.props`.
- `docker-compose.yml`, `docker/postgres/`, integration tests and test infrastructure; AI-DLC Nano state and audit.
