<!-- phase: DONE | branch: feature/issue-27-cors | tasks: 4/4
     base: 8146350 | updated: 2026-10-15
     next: commit/push/PR #27 on user approval; then #19 -->
# Plan: Gateway CORS
## Tasks
- [x] Cors:Sites (string[]) + Cors:AdminOrigins config; named policies; validate no "*" at startup
- [x] Site policy: GET/POST/OPTIONS, headers Content-Type + Idempotency-Key, no credentials; admin policy with credentials
- [x] UseCors on /health and /api/*, plus security-headers middleware
- [x] Config in appsettings (dev localhost origins; prod empty/env-provided)
## Tests
- Standard: integration tests in GatewaySmokeTests style: allowed site origin, unlisted origin, admin credentials only, preflight headers, /health, security headers
## Files
- src/Services/ApiGateway/{Program.cs,appsettings*.json}, tests/IntegrationTests/.../GatewayCorsTests.cs
