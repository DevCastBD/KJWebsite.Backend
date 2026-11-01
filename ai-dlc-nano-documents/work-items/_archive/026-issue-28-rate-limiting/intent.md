# Rate limiting on public write endpoints
- Source: issue #28 https://github.com/DevCastBD/KJWebsite.Backend/issues/28
- Docs: docs/MASTER_PLAN.md, acceptance criteria in issue
- Type: feature
## Request
- Gateway ASP.NET Core rate limiter: login fixed window 5/min/IP; register + cta submissions 10/hr/IP; global sliding window; 429 + Retry-After + Problem Details.
- Limits configurable per environment; client IP correct behind CDN via forwarded headers.
## Decisions
- Built-in Microsoft.AspNetCore.RateLimiting at the gateway, partitioned by client IP → assumed (no new package)
- Forwarded-headers trust list is configuration (KnownProxies/Networks), empty by default → assumed
## Out of scope
- Turnstile/honeypot anti-spam (#34)
## Follow-ups
- none yet
