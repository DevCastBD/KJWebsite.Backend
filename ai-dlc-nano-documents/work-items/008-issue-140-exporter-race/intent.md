# OpenAPI exporter builds services concurrently → flaky CI
- Source: issue #140 (found preparing the Dependabot batch, #141)
- Type: bug
## Request
- CI's OpenAPI step failed at random ("GenerateDepsFile task failed") on PRs #134/#136, whose changes cannot affect a build
## Decisions
- Root cause: four simultaneous `dotnet run`s each built the shared BuildingBlocks project → concurrent writes to the same bin/obj
- Fix (user approved with the merge plan): build each service once, serially, then `dotnet run --no-build`
## Out of scope
- The Dependabot PRs themselves (#141)
## Follow-ups
