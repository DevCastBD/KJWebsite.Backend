<!-- phase: DONE | branch: Feature/issue-47-public-extras | tasks: 5/5
     base: 6398470 | updated: 2026-10-31
     next: none (done) -->
# Plan: Public extras
## Tasks
- [x] search_terms table + SearchIndex (tokeniser, reindex on every content save via RevisionStore, startup backfill) + migration
- [x] GET /api/v1/content/search (lang, type, limit/offset; weighted title>summary>body; 2 s budget)
- [x] /sitemap.xml (published only, hreflang alternates) and /feed.rss + /feed.atom per language; gateway routes
- [x] OG metadata endpoint per item
- [x] Tests (conjuncts, mixed bn/en, drafts excluded, XML validity, per-language), docs, openapi, suite+PG, commit
## Tests
- Standard; Bengali conjunct/mixed queries; XML parsed back
## Files
- ContentService (Search, PublicExtras, migration), ApiGateway routes, tests, docs, openapi.v1.yaml
