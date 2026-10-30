# Observability: Serilog, health, metrics
- Source: issue #31 https://github.com/DevCastBD/KJWebsite.Backend/issues/31
- Docs: docs/MASTER_PLAN.md, ADR-011 (tracing deferred to #153)
- Type: feature
## Request
- Serilog JSON logs with correlation IDs across all services; /health/live|ready|startup per service; gateway aggregate; /metrics; PII redaction policy with a test.
## Decisions
- Traces deferred per ADR-011/#153; metrics via OpenTelemetry Prometheus exporter → assumed
- Never log request bodies; redaction destructuring policy for known PII fields
## Out of scope
- Distributed tracing, alerting rules (#153, ops)
## Follow-ups
- none yet
