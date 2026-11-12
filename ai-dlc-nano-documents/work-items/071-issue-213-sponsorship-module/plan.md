<!-- phase: WRAP-UP | branch: Feature/issue-213-sponsorship-module | tasks: 9/9
     base: 699a9bb | updated: 2026-11-12
     next: commit, push, open the PR (approved by the user for this ticket); never merge -->
# Plan: Sponsorship module (BE-106, #213)
## Tasks
- [x] SponsorshipService project, entities, DbContext (schema sponsorship), Program, solution and gateway wiring
- [x] Initial migration with audit immutability trigger
- [x] Students: list, create, get, patch (ETag, If-Match), project check, projects picker
- [x] Consent: append-only rows, form-before-grant rule, donor-view allow-list projection
- [x] Photo and signed form: private store, type, size and signature checks, metadata stripper
- [x] Module audit feed, audit config without personal data, read audit for guardian details
- [x] openapi.v1.yaml regenerated (additive), docs/SPONSORSHIP.md and DONORS.md updated
- [x] Integration tests (who may not read, consent order, files, audit, list, ETag)
- [x] Full suite: 1013 pass; only the 4 known artifacts-path failures
## Tests
- High blast radius (children, files, consent): tests lead with refusals and with what the log never holds.
## Files
- src/Services/SponsorshipService/**, ApiGateway/Program.cs, KJWebsite.Backend.slnx, tools/OpenApiExporter, openapi.v1.yaml
- tests/IntegrationTests/.../SponsorshipStudentTests.cs, docs/SPONSORSHIP.md, docs/DONORS.md
