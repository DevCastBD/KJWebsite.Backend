# Gateway CORS: allow If-Match and expose ETag (BE-116, #237)
- Source: issue #237 https://github.com/DevCastBD/KJWebsite.Backend/issues/237 (found while proving FE#362 live: Chromium blocked the PATCH preflight)
- Type: bug
## Request
- A browser on another origin cannot send If-Match (preflight refused) and cannot read the ETag response header, so every optimistic-concurrency edit is blocked: sponsorship students, finance donors, content and settings.
- Allow If-Match and expose ETag on both the site and the admin policy; change nothing else.
## Decisions
- Backend issue plus a small PR (not folded into #236); docs/GATEWAY_CORS.md is new because no gateway CORS doc exists → user chose 2026-11-19.
- Code and tests are small and written by Claude; docs and PR text by Codex → approved plan.
- Review fixes adopted: the ETag test now goes through a stubbed proxied cluster, preflight tests pin methods, max age and a refused header, the doc states content and settings return 409 (not 428 or 412) → independent review 2026-11-20.
## Out of scope
- Any other header or method, wildcard origins, credentials rules, rate limits.
## Follow-ups
- Expose Retry-After and X-Correlation-ID too (the frontend reads both cross-origin); decide on media X-Image-Width, X-Image-Height, X-Alt-En, X-Alt-Bn request headers.
- docs/MASTER_PLAN.md lines 23 and 30 still say the gateway has no CORS; one pre-existing em dash at CorsConfiguration.cs line 67.
