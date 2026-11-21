<!-- phase: WRAP-UP | branch: Feature/issue-239-expose-retry-after | tasks: 5/5
     base: b32dc56 | updated: 2026-11-21
     next: push and open the PR on the user yes (never merge) -->
# Plan: Expose Retry-After through gateway CORS (BE-117, #239)
## Tasks
- [x] CorsConfiguration.cs: ExposedHeaders gets Retry-After
- [x] GatewayCorsTests: exact exposed list, gateway 429 exposes Retry-After, unlisted origin unchanged
- [x] docs/GATEWAY_CORS.md and docs/MASTER_PLAN.md lines 23 and 30
- [x] Full suite, format, mutation checks, reviewer
- [x] Live cross-origin check of Retry-After
## Tests
- Standard, security-adjacent: nothing else widened.
## Files
- src/Services/ApiGateway/CorsConfiguration.cs, tests/IntegrationTests/KJWebsite.IntegrationTests/GatewayCorsTests.cs, docs/GATEWAY_CORS.md, docs/MASTER_PLAN.md
