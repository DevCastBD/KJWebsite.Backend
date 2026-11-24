<!-- phase: DONE | branch: Feature/issue-242-content-conflict-codes | tasks: 6/6
     base: 00a82e6 | updated: 2026-11-24
     next: commit locally, ask the user before push and PR -->
# Plan: Content distinct 409 codes (Backend#242)
## Tasks
- [x] UpsertResult.Code + ContentErrorCodes; set SLUG_TAKEN (rename, tag create) and TAG_NAME_TAKEN
- [x] POST create refuses an existing slug (live or deleted) with SLUG_TAKEN, overwrites nothing
- [x] ContentHttp.Failure takes the result's code and every caller passes the result (bulk keeps CONFLICT: no bulk path can produce the new codes)
- [x] Integration tests, existing assertions kept; mutation checks
- [x] Docs (IDEMPOTENCY, BACKOFFICE_APIS, content contract) and endpoint descriptions
- [x] Live proof through the gateway, reviewer, PR text
## Tests
- Standard feature: full dotnet test, mutations, live against the local stack.
## Files
- src/Services/ContentService/{ContentHttp,GenericContentEndpoints,SettingsEndpoints,Program,BackOfficeEndpoints}.cs, Data/{ContentStore,GenericContentStore,TaxonomyStore}.cs, tests/IntegrationTests/.../Content*Tests.cs, docs/
