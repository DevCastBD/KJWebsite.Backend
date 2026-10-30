# Observability

Implemented in `BuildingBlocks/Observability` and wired into every service with `builder.AddKjObservability("<Service>")`, `app.UseKjObservability()` and `app.MapKjObservability()` (ADR-011).

## Logs

Serilog, one JSON object per line on stdout (`RenderedCompactJsonFormatter`: `@t`, `@m`, `@l`, plus properties). Every event carries `Service` and, inside a request, `CorrelationId` (from `X-Correlation-Id`, see [GATEWAY.md](GATEWAY.md)). Search one request end to end by its correlation ID across the gateway and service logs.

Levels: `Information`; `Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore` and `System.Net.Http` at `Warning` (EF would otherwise log SQL parameter values). Override with a `Serilog:MinimumLevel` section.

### Personal data

Applicant records hold names, phones, addresses and blood groups, so redaction is built in, not reviewed later (`PiiRedactionEnricher`, the last enricher):

- Properties whose name contains `email`, `phone`, `password`, `token`, `authorization`, `name`, `address`, `bloodgroup`, `dateofbirth`, `values`, … become `[REDACTED]`, including members of destructured objects, dictionaries and sequences.
- Emails, JWTs, `Bearer …` tokens and phone numbers (`+…` or leading `0`) inside any string are scrubbed.
- Request and response bodies are never logged. Request logging records method, path, status and duration only.
- Exception messages from libraries are not rewritten; do not put personal data in exception text.

`tests/IntegrationTests/.../LoggingRedactionTests.cs` proves it. Add a name to `SensitiveNames` when a new field carries personal data.

## Health

| Endpoint | Meaning |
|---|---|
| `/health/live` | process is up (`self`) |
| `/health/ready` | can serve: database reachable (gateway: downstream services, see below) |
| `/health/startup` | migrations done and database reachable |
| `/health` | legacy `{status, service}`, kept |

`200` Healthy or Degraded, `503` Unhealthy; the body lists each check and status, never exception text. The gateway's `/health/ready` probes every destination's `/health/ready`: some down is Degraded, none up is Unhealthy. Its cluster health checks use the same path, so a not-ready service stops receiving traffic.

## Metrics

`GET /metrics` (Prometheus text; OpenTelemetry ASP.NET Core and runtime instrumentation). It answers only to loopback or networks in `Observability:MetricsAllowedNetworks` (CIDR list), and never to a request carrying `X-Forwarded-For`; everyone else gets `404`. The gateway has no route to it, so it is not reachable through the public API. The Prometheus exporter package is a pinned pre-release (`1.20.1-beta.1`); it has no stable release.

Alerting signals (error rate, latency, queue depth, backup failure) are wired for §8 operations as: `http_server_request_duration_seconds` (rate/latency), `/health/ready` status, outbox dead-letter count (#33) and backup status (#35).

## Not yet

Distributed tracing (OTLP) is deferred to #153; correlation IDs in logs are the substitute until then.
