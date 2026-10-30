<!-- phase: DONE | branch: Feature/issue-38-content-model-taxonomy-redirects | tasks: 6/6
     base: ac60cb4 | updated: 2026-10-31
     next: none (done) -->
# Plan: Generalised content model, taxonomy, redirects
## Tasks
- [x] ContentTypeRegistry (8 types: route segment, statuses); generic items API for page/faq/team-member/testimonial/partner/press: public GET list/detail, admin CRUD with ETag/If-Match, soft delete, cache eviction
- [x] Tag + TagTranslation + ContentItemTag: Bengali-aware key normalisation (NFC, case, ZWJ/ZWNJ, digits); admin tag CRUD; items take tags by slug; public ?tag= filter
- [x] ContentRelation: admin sets related items (any type); public detail returns related in requested lang
- [x] Redirect table + admin CRUD + public resolve endpoint; admin slug rename inserts redirect (flattens chains, rejects loops/collisions)
- [x] EF migration; tests (types, tags bn/en, related, rename redirect, legacy .html/.php, permissions); docs/CONTENT_MODEL.md; openapi regen
- [x] Full suite, format, openapi verify, commit
## Tests
- Standard: WebApplicationFactory/SQLite; PG migration test if KJ_TEST_POSTGRES set (not run here)
## Files
- src/Services/ContentService/{ContentTypes,Data/*,Contracts,Program/endpoints,Migrations}, tests, docs, openapi.v1.yaml
