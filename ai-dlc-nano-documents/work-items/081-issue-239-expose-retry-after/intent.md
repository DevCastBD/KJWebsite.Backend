# Expose Retry-After through the gateway CORS policy (BE-117, #239)
- Source: issue #239 https://github.com/DevCastBD/KJWebsite.Backend/issues/239 (follow-up to #237 and PR #238)
- Type: bug
## Request
- A browser on another origin cannot read Retry-After (rate limiter, idempotency middleware, finance campaign, payment webhook), so the frontend ignores the server's wait and uses its own backoff.
- Expose Retry-After on both CORS policies; correct the stale "no CORS" lines in docs/MASTER_PLAN.md; name it in docs/GATEWAY_CORS.md.
## Decisions
- Follow-up fix PR with its own issue; X-Correlation-ID dropped (the backend never sends it), media upload headers out (no media endpoint yet) → user chose 2026-11-21.
- Code and tests by Claude; issue text, docs and PR text by Codex or a Claude subagent when Codex stalls at startup → approved plan.
- Review fixes adopted: MASTER_PLAN sentence makes no claim about other gaps, rate limit test sends exactly two requests, unlisted origin 429 covered → independent review 2026-11-21.
- Codex stalled again on the issue text; a Claude subagent wrote it, Codex wrote the docs and PR text → noted for the user.
## Out of scope
- X-Correlation-ID echo, X-Image-* and X-Alt-* request headers, any other header or method, wildcard origins, credentials rules.
## Follow-ups
- docs/MASTER_PLAN.md section 1 (the service table, Four services, the do-not-deploy note) still describes the P0 snapshot and needs a full refresh.
- GATEWAY_CORS.md could name the status code for each Retry-After sender (409, 503, 503).
