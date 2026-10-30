# Back-office APIs: dashboard, search, bulk actions (BE-038, #44)

All under `/api/v1/admin/content`, scoped to ContentService (the only service holding these records on this branch). Submissions, media and user widgets plug in when those modules land; the response shapes only ever gain fields.

## Dashboard — `GET /dashboard` (`dashboard.view`)
Item counts per type and status, the `awaitingReview` queue (oldest first, 20), `upcomingScheduled` (soonest first, 20), `translationGaps` totals (per type and language), `trashCount`, and `recentActivity` (last 10 audit events) **only for callers with `audit.view`**. Cached in process for 30 s (`BackOffice:DashboardCacheSeconds`, 0 disables) in two variants (with/without activity), so it is cheap on every page view and a cached copy with activity is never served to a caller without `audit.view`. The cache is per instance; once P7 adds aggregates, read those instead of the transactional tables.

## Search — `GET /search?q=&limit=` (`dashboard.view`)
Case-insensitive substring over item slugs, titles and summaries (any language), tags (slug or name in either language), redirect paths and setting keys. **Only kinds the caller may read are searched**: items, tags and redirects need `content.view`, settings need `settings.view`. `searched` lists the kinds covered; the rest are omitted, so a result never confirms a record the caller cannot open. `q` is 2-100 characters, `limit` 1-50 (default 20); results are ordered exact, prefix, then contains. Bengali full-text ranking is #47.

## Bulk — `POST /bulk` (`content.view`, then per action)
`{ "action": "...", "items": [{ "type": "page", "slug": "about" }], "tag": "...", "publishAt": "..." }`, 1-100 items (duplicates processed once). Actions: `workflow:draft|in_review|scheduled|published|archived`, `delete`, `restore`, `tag-add`, `tag-remove`.
The response is always `200` with `{ total, succeeded, failed, results: [{ type, slug, status: ok|failed, code, message }] }`; codes: `NOT_FOUND`, `INVALID`, `FORBIDDEN`, `CONFLICT`, `ERROR`. Each item runs in its own scope and transaction, goes through the same stores and permission rules as the single-item endpoints (delete needs `content.delete`, publishing needs `content.publish`, the rest `content.update`), is audited, and a failure never stops the others. The public cache is evicted afterwards.
