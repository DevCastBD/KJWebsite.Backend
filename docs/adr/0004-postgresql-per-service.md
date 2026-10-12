# ADR-004: PostgreSQL 17 with service-owned databases and schemas

- Status: Accepted
- Date: 2026-10-12
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #21](https://github.com/DevCastBD/KJWebsite.Backend/issues/21), [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12)

## Context

Auth and CTA currently use SQLite for local runs and `EnsureCreated` against PostgreSQL in Compose. SQLite migrations cannot be applied to PostgreSQL. The services need durable, independently owned storage and a repeatable migration path before further P1 schema work.

## Decision

Use PostgreSQL 17 and the Npgsql EF Core provider for every persistent service. Auth owns the `auth` database and `auth` schema; CTA owns the `cta` database and `cta` schema. They may share a PostgreSQL server, but each service owns its connection, tables, migrations, and migration-history table. Content remains in memory until its separate persistence work item.

Use EF migrations, not `EnsureCreated`, for fresh databases and upgrades. Keep migrations compatible with the preceding application release when evolving a PostgreSQL schema. Existing SQLite development data follows a backup, export, import, verify, and cutover procedure; keep the SQLite source intact for rollback until verification succeeds.

PostgreSQL 18 was considered because it is newer, but 17 matches the ratifier's installed 17.10 instance and the existing Compose and test images. Review the major version separately before 17 reaches end of support.

## Rejected alternatives

- Continue SQLite: it cannot meet the multi-service production storage requirements.
- Share one database and schema across services: this weakens ownership and makes independent migration and hosting harder.
- Continue `EnsureCreated`: it has no incremental migration history and cannot manage later schema changes.

## Consequences

- PostgreSQL is required for Auth and CTA development and integration tests.
- Database provisioning, backups, collation, migration ordering, and data transfer need explicit operational instructions.
- Separate databases retain the option to host services apart later without changing their data ownership.
