# Background jobs and the transactional outbox

Hangfire (ADR-005) runs the jobs; the outbox decides what must happen. Code: `BuildingBlocks/Outbox`, `BuildingBlocks/Jobs`. First user: `CtaSubmissionService`.

## Writing a side effect

```csharp
db.Submissions.Add(entity);
db.Enqueue("cta.submission.received", new { submissionId = entity.Id });   // same context
await db.SaveChangesAsync();                                                // one transaction
outbox.Signal();                                                            // optional: wake the dispatcher now
```

The message row commits or rolls back with the business data, so an event cannot be lost after a successful write or fire for a rolled-back one. Payloads carry ids, never personal data; the handler loads what it needs.

A service opts in with: a `DbContext` implementing `IOutboxDbContext` and calling `modelBuilder.ConfigureOutbox()`, a migration, `AddKjBackgroundJobs<TContext>(config, "<ConnectionName>")`, one `AddOutboxHandler<T>()` per message type, and `app.MapKjBackgroundJobs<TContext>()`.

## Delivery

A recurring Hangfire job (every minute) and the `Signal()` nudge run `OutboxDispatcher`. It claims each due message with one conditional `UPDATE` (a lease), so several instances can run without double delivery; a crashed instance's lease lapses after `Outbox:Lease` (5 min). Delivery is at-least-once: **handlers must be idempotent**.

On failure the message is retried with exponential backoff: `BaseDelay` (30 s) × 2^(attempt-1), capped at `MaxDelay` (1 h), ±10 % jitter. After `MaxAttempts` (8) it is copied to `dead_letters` and retired. A message type with no handler is treated as a failure, so it is dead-lettered rather than silently dropped. Processed rows are purged after `Retention` (7 days). All settings are in `Outbox:*`.

## Dead letters: owner and alert

`dead_letters` rows with `ResolvedAt IS NULL` are failures nobody has dealt with.

- **Alert:** the dispatcher logs an `Error` ("dead-lettered") and increments `kj_outbox_dead_lettered` (Prometheus, `/metrics`). Alert on any increase or any unresolved row.
- **Owner:** the on-call operator named in `docs/RUNBOOK.md` (#35). They fix the cause, re-queue by inserting a new outbox row from the dead letter's `Type`/`Payload` (or discard it), and set `ResolvedAt`.

```sql
select "Id", "Type", "Attempts", "Error", "FailedAt" from cta.dead_letters where "ResolvedAt" is null;
```

## Dashboard

`/hangfire` on the service itself. It shows job payloads, so it needs a bearer token with `roles.manage` (SuperAdmin) and is **not routed through the gateway**: call it from the host or a tunnel with `Authorization: Bearer …`. Hangfire stores its state in the `hangfire` schema of the service's own database.

## Configuration

| Setting | Default | Notes |
|---|---|---|
| `Jobs:Storage` | `PostgreSql` | `InMemory` for tests only |
| `Jobs:Schema` | `hangfire` | |
| `Jobs:ServerEnabled` | `true` | `false` runs no worker in that process |
| `Jobs:WorkerCount` | `2` | |
| `Outbox:*` | see above | `MaxAttempts`, `BaseDelay`, `MaxDelay`, `Jitter`, `BatchSize`, `Lease`, `Retention` |

OpenAPI export (`OpenApi:Export`) skips Hangfire because it runs without a database.
