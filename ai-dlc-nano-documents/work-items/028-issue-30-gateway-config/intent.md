# Gateway routing from configuration
- Source: issue #30 https://github.com/DevCastBD/KJWebsite.Backend/issues/30
- Docs: docs/MASTER_PLAN.md, ADR-001
- Type: feature
## Request
- YARP routes/clusters in appsettings.{Environment}.json; active health checks gate destinations; correlation ID (X-Correlation-Id) generated/propagated; response compression for public reads.
## Decisions
- Health check path /health/ready (from #31, until then /health) → assumed
- Correlation ID header name X-Correlation-Id → assumed
## Out of scope
- Service-side logging of the ID (#31)
## Follow-ups
- none yet
