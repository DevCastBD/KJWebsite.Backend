<!-- phase: WRAP-UP | branch: Feature/issue-237-cors-if-match-etag | tasks: 5/5
     base: 40d4c25 | updated: 2026-11-20
     next: push and open the PR on the user yes (never merge); frontend PR #366 depends on it -->
# Plan: Gateway CORS If-Match and ETag (BE-116, #237)
## Tasks
- [x] CorsConfiguration.cs: allow If-Match, expose ETag on both policies
- [x] GatewayCorsTests: preflight allows If-Match (site, admin), unlisted refused, ETag exposed, credentials unchanged
- [x] docs/GATEWAY_CORS.md (Codex) and the PR text (Codex)
- [x] Full suite, format, mutation checks, reviewer
- [x] Live proof with the FE#362 build and no local patch
## Tests
- Standard, security-adjacent: tests assert nothing else widened (credentials, unlisted origins).
## Files
- src/Services/ApiGateway/CorsConfiguration.cs, tests/IntegrationTests/KJWebsite.IntegrationTests/GatewayCorsTests.cs, docs/GATEWAY_CORS.md
