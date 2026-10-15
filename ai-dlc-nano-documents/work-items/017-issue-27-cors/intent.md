# CORS allow-list at the gateway (BE-004)
- Source: issue #27 (closes G5; verified by #19)
- Type: feature
## Request
- CORS policy at ApiGateway driven by per-environment config allow-list; never AllowAnyOrigin
- Credentials only for the admin origin; unlisted origins rejected
- Allow Idempotency-Key + Content-Type headers; GET /health probe reachable from site origin
- Security headers (X-Content-Type-Options, Referrer-Policy, Permissions-Policy) at gateway
## Decisions
- Plan v2: no preview-deployment origins; exact-match origins from config (pending confirm)
## Out of scope
- Preview-origin patterns; rate limiting; #30 config-driven routing
## Follow-ups
- #30 will move routes into config; keep Cors section when it does
- Preview-origin pattern deferred (plan v2 drops previews)
