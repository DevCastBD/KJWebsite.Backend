<!-- phase: DONE | branch: Feature/issue-40-translation-completeness | tasks: 5/5
     base: 0b2f9be | updated: 2026-10-31
     next: none (done) -->
# Plan: Translation completeness and fallback
## Tasks
- [x] Required fields per type + TranslationCompleteness (per item/lang); completeness in generic admin view + GET items/{type}/{slug}/completeness
- [x] Report: GET admin/content/translations/report (filters type, lang, status; gaps across all types; summary counts)
- [x] TranslationPolicy from config (hide | fallback | placeholder per requested language) applied to public reads of all 8 types; additive translationPending/fallbackFrom fields
- [x] Publish warnings (and optional enforcement) in workflow response
- [x] Tests, docs/TRANSLATIONS.md (editor-facing), openapi, full suite + PG, commit
## Tests
- Standard: SQLite + PG run; public contract stays additive
## Files
- ContentService (ContentKinds, new TranslationCompleteness/TranslationPolicy, ContentStore, GenericContentStore, endpoints), tests, docs, openapi.v1.yaml
