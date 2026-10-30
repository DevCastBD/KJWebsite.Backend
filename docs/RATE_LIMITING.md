# Rate limiting

The gateway throttles per client IP (ASP.NET Core rate limiter, `src/Services/ApiGateway/RateLimitingConfiguration.cs`).

| Scope | Default | Window |
|---|---|---|
| `POST /api/v1/auth/login` | 5 | 60 s fixed |
| `POST /api/v1/auth/register` | 10 | 1 h fixed |
| `POST /api/v1/cta/submissions` | 10 | 1 h fixed |
| Every request | 300 | 60 s sliding |

A throttled request gets `429` with `Retry-After` (seconds) and an RFC 9457 body (`urn:kjwebsite:error:rate-limited`), in Bengali when `?lang=bn` or `Accept-Language: bn`. It also keeps the legacy `error` object.

Rate limiting stops the flood; Identity lockout (#22) protects one account.

## Configuration

`RateLimiting:*` (`Enabled`, `Global`, `Login`, `Register`, `CtaSubmission`, each `PermitLimit` + `WindowSeconds`) can be overridden per environment (appsettings or environment variables such as `RateLimiting__Login__PermitLimit`).

## Client IP behind a CDN

`X-Forwarded-For` is honoured only from trusted peers; otherwise every client behind the CDN shares one address, or a client can spoof its own.

- `ForwardedHeaders:KnownProxies` / `ForwardedHeaders:KnownNetworks` (CIDR): list the CDN or reverse-proxy addresses.
- `ForwardedHeaders:TrustAnyProxy=true`: only when the gateway is reachable solely through the proxy.
- Neither set: forwarded headers are trusted from loopback only (the ASP.NET Core default).

Set one of these before the first public deployment.
