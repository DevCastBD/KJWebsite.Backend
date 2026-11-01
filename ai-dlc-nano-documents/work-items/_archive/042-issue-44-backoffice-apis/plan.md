<!-- phase: DONE | branch: Feature/issue-44-backoffice-apis | tasks: 4/4
     base: 45618ff | updated: 2026-10-31
     next: none (done) -->
# Plan: Back-office APIs
## Tasks
- [x] GET admin/content/dashboard: counts by status per type, in_review queue, upcoming scheduled, translation-gap totals, trash size, recent activity (audit.view); memory-cached 30 s per permission set
- [x] GET admin/content/search?q=: items (slug, title/summary, any language), tags, redirects, settings keys; each kind only with its permission (content.view, settings.view); relevance order; limit
- [x] POST admin/content/bulk: action (workflow:<state>, delete, restore, tag-add, tag-remove) over up to 100 items; per-item {type, slug, status ok|failed, code, message}; HTTP 200 with summary counts; each item permission-checked and audited via the same stores
- [x] Tests (summary correctness+cache, search permission filtering, partial bulk success), docs/BACKOFFICE_APIS.md, openapi, suite+PG, commit
## Tests
- Standard + permission-leak tests
## Files
- ContentService (BackOfficeStore, endpoints), tests, docs, openapi.v1.yaml
