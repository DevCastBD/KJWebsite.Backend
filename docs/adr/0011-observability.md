# ADR-011: Structured logs now, distributed tracing deferred

- Status: Accepted
- Date: 2026-10-15
- Ratified by: Fahimul Hoque (@FHShubho)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12), [issue #153](https://github.com/DevCastBD/KJWebsite.Backend/issues/153)

## Context

The plan recommended OpenTelemetry exported over OTLP plus Serilog. Plan v2 (2026-10-12) defers distributed tracing, because it only pays off once services run on separate hosts (ADR-001, #153). Structured logging and health checks are not deferred and ship with the platform core (#31).

## Decision

Use Serilog for structured JSON logs with correlation IDs, and expose `/health/live` and `/health/ready`, from P1. Personal data must never be logged.

Adopt OpenTelemetry (traces and metrics exported over OTLP) as the target, but defer its rollout to #153, triggered when a service is first deployed to a separate host or domain.

## Rejected alternatives

- OpenTelemetry tracing from the start: roughly 30% extra effort with no benefit while every service is co-hosted behind one gateway.
- Plain console logging: no correlation across gateway and services, and hard to query.

## Consequences

- Until #153 lands there is no trace spanning gateway to service; correlation IDs in logs are the substitute.
- Log shape and correlation-ID propagation are fixed now so adding OpenTelemetry later is additive.
