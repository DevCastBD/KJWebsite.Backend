<!-- phase: DONE | branch: 8-feature-b0-7-populate-buildingblocks-problemdetails-result-paging-language-resolution | tasks: 6/6
     base: caf8805 | updated: 2026-09-20
     next: complete; no commit, push, or tracker update requested -->
# Plan: Populate BuildingBlocks
## Tasks
- [x] Replace `Class1` with shared Result, paging envelope, language resolver, Problem Details factory, compatibility error, and endpoint-filter base.
- [x] Create an xUnit BuildingBlocks test project and add it to the solution.
- [x] Cover language query/header/default precedence and paging envelope serialization.
- [x] Adopt shared errors in AuthIdentityService and CtaSubmissionService; remove local helpers.
- [x] Adopt shared errors, language resolution, response language, and paging in ContentService; remove local helpers.
- [x] Build, test, format, verify runtime Content responses, and regenerate/verify OpenAPI if its contract changes.
## Tests
- Standard rigor: new unit tests plus `dotnet test KJWebsite.Backend.slnx`, build, format verification, OpenAPI verification, and a live Content endpoint check.
## Files
- `src/BuildingBlocks/**`, `tests/UnitTests/**`, `KJWebsite.Backend.slnx`
- `src/Services/{AuthIdentityService,ContentService,CtaSubmissionService}/Program.cs`
- `openapi.v1.yaml` only if regenerated output differs
