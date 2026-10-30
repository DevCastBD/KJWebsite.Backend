# Idempotency, output caching and optimistic concurrency (BE-030, #48)

## Idempotency-Key (closes G27)
`POST /api/v1/cta/submissions` honours the `Idempotency-Key` header (opt in per endpoint with `.RequireIdempotency()`, middleware in `KJWebsite.BuildingBlocks/Idempotency`).

| Situation | Result |
|---|---|
| No header | Processed normally, never deduplicated |
| Same key + same body, first request succeeded | Original status, body and `Location` replayed, plus `Idempotency-Replayed: true`; nothing is created |
| Same key + different body | `422 IDEMPOTENCY_KEY_REUSED` |
| Same key while the first request is still running | `409 IDEMPOTENCY_IN_PROGRESS`, `Retry-After: 1` |
| First attempt failed (non-2xx or exception) | Key released, the retry runs normally |
| Malformed key (not one printable-ASCII value, 1-255 chars) | `400 VALIDATION_ERROR` |

- **Window:** 24 hours (`Idempotency:WindowHours`). The frontend sends one key per form session, so a day covers any retry. A claimed key blocks duplicates for 30 s while its first request runs.
- **Scope:** method + path + key, hashed; the body fingerprint is SHA-256. Bodies over 256 KB are refused (413).
- **Store:** process memory by default (correct for one instance). With `Redis:ConnectionString` the store is Redis (`SET NX`, shared between instances). The Redis path is not exercised by the test suite (no Docker at this stage).
- P4 webhooks and donations reuse the same middleware.

## Output cache (public content)
Public `GET /api/v1/content/*` responses are cached for 60 s in the `public-content` policy, varied by every query value (`lang`, paging, status) and `Accept-Language`. Any successful admin write evicts the `content` tag, so a publish is visible at once. Redis (`Redis:ConnectionString`) shares the cache between instances.

## ETag / If-Match (admin content)
- Admin `GET .../{id}` and every admin write return an `ETag` (the item's `UpdatedAt` at microsecond precision).
- `PUT .../{id}` with `If-Match` that does not match answers `409 CONFLICT` and saves nothing. `UpdatedAt` is also the EF concurrency token, so two saves racing on the same version cannot both win.
- `If-Match` is optional: without it the last write wins, so existing clients keep working. `*` always matches.
