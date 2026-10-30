<!-- phase: DONE | branch: Feature/issue-45-site-settings | tasks: 4/4
     base: 31fec26 | updated: 2026-10-31
     next: none (done) -->
# Plan: Site settings
## Tasks
- [x] Entities settings + feature_flags, migration, audit registration
- [x] Typed catalog (navigation.main/footer, contact, social, footer.text, homepage.sections, maintenance) + custom.*; bn+en required; localisation
- [x] Admin API (settings.view/manage, ETag/If-Match, flags CRUD) + public GET settings/flags (lang, cached, secrets excluded) + bootstrap settings filled
- [x] Tests, docs/SITE_SETTINGS.md, openapi, suite + PG, commit
## Tests
- Standard + security (secret leakage); PG run
## Files
- ContentService (Data, endpoints, migration), tests, docs, openapi.v1.yaml
