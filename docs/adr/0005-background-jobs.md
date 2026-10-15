# ADR-005: Hangfire for background jobs, with a transactional outbox

- Status: Accepted
- Date: 2026-10-14
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12)

## Context

No background-job infrastructure exists yet. Upcoming work (CTA acknowledgement/notification email, membership and donation processing, report generation) needs retryable, observable async work that survives a process restart — plain `Task.Run` or fire-and-forget calls do not.

## Decision

Use Hangfire for background job scheduling and execution, with its own persistent job storage. Pair it with a transactional outbox: a side effect that must follow a database write (e.g. "send acknowledgement email after a CTA submission is saved") is recorded in the same transaction as the business write, and a dispatcher enqueues the Hangfire job from the outbox, so the job is never lost to a crash between the write and the enqueue.

## Rejected alternatives

- Quartz.NET: comparable scheduling capability, but no built-in dashboard for visibility into job status and failures, which matters for a small/volunteer team with no dedicated ops tooling.
- Ad hoc `Task.Run` or `IHostedService` background work: no persistence, no retry policy, and failures are invisible — exactly the gap this decision closes.
- Enqueueing the job directly in the request handler without an outbox: risks a job firing for a write that is later rolled back, or being lost if the process crashes after the write but before the enqueue.

## Consequences

- Hangfire needs its own storage (a dedicated schema, following the per-service-schema pattern from ADR-004) and its dashboard must be secured behind authentication before any public exposure.
- Services that need deferred or retried work implement the outbox-write-then-dispatch pattern rather than calling Hangfire directly from request handlers.
