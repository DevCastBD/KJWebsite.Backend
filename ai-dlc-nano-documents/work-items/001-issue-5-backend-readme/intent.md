# Backend README with security notice
- Source: issue #5 https://github.com/DevCastBD/KJWebsite.Backend/issues/5
- Type: feature
## Request
- Replace the scaffold README with the shared-template backend README, accurate to the current repository.
- Document architecture, local development, services, configuration, data/migrations, API contract, legacy snapshots, and future deployment.
- Prominently state the current auth/security limitations and never-reuse development seed credentials.
- Include a Mermaid architecture diagram; do not add badges unsupported by existing workflows.
## Decisions
- OpenAPI contract maintenance → add and document an automated export process in this item (user confirmed).
- Export approach → a cross-platform .NET CLI starts development services, merges runtime OpenAPI documents, writes YAML, and offers verification (user confirmed).
## Out of scope
- Implementing Docker, CI, security hardening, or PostgreSQL.
## Follow-ups
- Existing package advisories: Microsoft.OpenApi 2.0.0 and SQLitePCLRaw.lib.e_sqlite3 2.1.11 are high severity (found during 001).
