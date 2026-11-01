<!-- phase: DONE | branch: feature/issue-26-content-contract | tasks: 5/5
     base: 2c6c433 | updated: 2026-10-15
     next: commit on approval; then #20 -->
# Plan: Content contract + bootstrap
## Tasks
- [x] Projects list: paging + envelope with lang; keep news shape
- [x] GET /content/bootstrap (projects, news, empty settings, lang)
- [x] ETag (hash of body) + 304 on If-None-Match; Cache-Control public,max-age on public reads
- [x] Admin GET list/by-id returning all translations grouped per slug
- [x] Regenerate openapi.v1.yaml with tools/OpenApiExporter
## Tests
- Standard: integration tests in ContentSmokeTests style: envelope fields, paging validation, bootstrap shape, ETag/304, admin auth + all translations
## Files
- src/Services/ContentService/Program.cs, tests/.../ContentContractTests.cs, openapi.v1.yaml
