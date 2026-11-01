# Persist content (#25, closes #18)
- Source: issue #25 (also closes G4 #18)
- Docs: docs/MASTER_PLAN.md §1.2 G4, §3.2, §5 B1-5; frontend snapshots at KJWebsite.FrontEnd/src/content/snapshots (sibling repo, commit 2c20a78)
- Type: feature
## Request
- ContentItem/ContentTranslation/Project/NewsArticle tables + migration; seeder from frontend data; both languages incl. Bengali; unique stable slugs; tests for create/read/update/soft-delete.
## Decisions
- Approved with the #22 run (stacked #22→#25).
- Public API unchanged except additive fields (news tags; per-translation category/year/date); documented in docs/CONTENT_CONTRACT.md.
- Seed = frontend snapshots only (embedded JSON); the two hard-coded sample entries were placeholders for the same slugs and are superseded. Dev-only news placeholders not imported. Seeds only an empty database.
- Soft delete keeps slug reserved; POST restores. Slug regex, en/bn langs, per-type status sets validated (400).
- News public only when published; bn collation (bn-x-icu) on Title, as CTA; no tsvector yet (P2 search).
- OpenApiExporter now runs ContentService in export mode (no DB).
## Out of scope
- Settings store (bootstrap settings still empty), editorial workflow, search (P2).
## Follow-ups
- DB-side paging for public lists once content grows (currently loads then pages in memory).
