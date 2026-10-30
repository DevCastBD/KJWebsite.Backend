<!-- phase: DONE | branch: feature/issue-25-content-persistence | tasks: 6/6
     base: b917190 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: persist content
## Tasks
- [x] Entities, ContentDbContext (content schema, bn collation), InitialContent migration
- [x] ContentStore (read/upsert/soft delete, validation) and Program.cs on the store
- [x] Embedded seed JSON + ContentSeeder (empty DB only)
- [x] Tests: seed intact both langs, CRUD+soft-delete, restart/replica, slug rules, PG migration+collation
- [x] OpenAPI regen (exporter covers Content), docs (README, contract, map)
- [x] Full suite + format
## Tests
- Standard: 94 tests pass (SQLite + local PG 18.6)
## Files
- src/Services/ContentService/**, tests/IntegrationTests/**, tools/OpenApiExporter, docs, README
