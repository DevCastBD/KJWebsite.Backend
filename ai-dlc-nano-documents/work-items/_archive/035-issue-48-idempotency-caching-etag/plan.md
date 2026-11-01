<!-- phase: DONE | branch: Feature/issue-48-idempotency-caching-etag | tasks: 6/6
     base: 2c9bc40 | updated: 2026-10-31
     next: none (done) -->
# Plan: Idempotency, output caching, ETag/If-Match
## Tasks
- [x] BuildingBlocks: IdempotencyMiddleware + IIdempotencyStore (memory, Redis), 24h TTL, opt-in via `.RequireIdempotency()`; 422 on key reuse with different body, 409 while in flight
- [x] CTA submissions endpoint opts in; replay returns original 201 + `Idempotency-Replayed: true`
- [x] Content: output-cache policy (vary lang/Accept-Language, tag "content") on public GETs; evict on admin upsert/delete
- [x] Content: ETag (from id+UpdatedAt) on admin GETs/PUT responses; PUT honours If-Match -> 409 CONFLICT
- [x] Tests (unit store/middleware, integration CTA replay, cache per-lang+invalidation, 409), docs/IDEMPOTENCY.md, openapi regen
- [x] Full suite, format, openapi --verify
## Tests
- Standard/high: unit + WebApplicationFactory integration (SQLite); Redis paths not exercised (no Docker)
## Files
- src/BuildingBlocks/.../Idempotency/*, src/Services/CtaSubmissionService/Program.cs, src/Services/ContentService/Program.cs + Data/ContentStore.cs, tests/*, docs/IDEMPOTENCY.md, openapi.v1.yaml
