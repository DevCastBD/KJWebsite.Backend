<!-- phase: DONE | branch: feature/issue-28-rate-limiting | tasks: 5/5
     base: 6608350 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: Rate limiting on public write endpoints
## Tasks
- [x] RateLimitingOptions + appsettings section (per env)
- [x] Policies + route mapping (login, register, cta) + global sliding window, 429 Problem Details with Retry-After
- [x] Forwarded headers config
- [x] Tests: throttle after N, Retry-After, per-IP isolation, config override, X-Forwarded-For
- [x] Docs, openapi (429), full suite + format
## Tests
- Standard: integration tests on gateway factory (low limits via config)
## Files
- src/Services/ApiGateway/**, tests/IntegrationTests, docs
