# Editorial workflow (BE-034, #39)

One status machine for all eight content types: `draft → in_review → scheduled → published → archived`. Projects keep their legacy live status `active` in storage (the API accepts `published` and stores `active`).

| From | To |
|---|---|
| draft | in_review, scheduled, published |
| in_review | draft, scheduled, published |
| scheduled | draft, scheduled (reschedule), published |
| published | draft, archived |
| archived | draft |

`POST /api/v1/admin/content/items/{type}/{slug}/workflow` `{ "to": "...", "publishAt": "..." }` (honours `If-Match`).

**Who may do what (review rule; frontend decision D-16 is still open).** Sending to review and pulling back to draft need `content.update`. Every move into or out of scheduled, published and archived needs **`content.publish`**. The same rule guards POST/PUT: changing `status` into or out of a live, scheduled or archived state without `content.publish` is a 403, so creating "published" content can no longer bypass review. Scheduling has its own endpoint (it needs a time); POST/PUT with `status: scheduled` is a 400. Editing a live item without changing its status needs only `content.update`.

## Scheduled publishing
`scheduled` needs a future `publishAt`. `ScheduledPublisher` (a hosted service in ContentService) polls every 30 s (`Publishing:PollSeconds`, `Publishing:Enabled`) and once at startup, so a schedule missed while the service was down is published late, not lost. A failed publish leaves the item scheduled and is retried at the next poll; each item has its own scope so one failure cannot block others. With several instances, the item's `UpdatedAt` concurrency token makes the loser's save fail harmlessly. On publish: the output cache is evicted, `publishedAt` = the scheduled time, a revision (`scheduled-publish`) is written, and the change is audited as `system:scheduler`.
This is self-contained; the P1 job runner (#33) is not on this base. When it lands, move `PublishDueAsync` onto it (the method is already a single idempotent pass).

## Revisions
Every content change (create, update, tags, related, rename, workflow move, restore, undelete, scheduled publish) stores a full snapshot (all languages, images, type-specific fields, tags, related); an identical snapshot is not stored twice.
- `GET .../items/{type}/{slug}/revisions` (newest first), `.../revisions/{n}`, `.../revisions/diff?from=&to=` (defaults: latest vs. the one before). The diff is field-level and per language: `translations.bn.title`, `translations.en.body`, `project.icon`, `tags`, `related`, …
- `POST .../revisions/{n}/restore` applies the snapshot as a new revision (`restore:n`). The slug, status and publication date are not rolled back; tags and related items deleted since are skipped.

## Trash
Delete is soft. `GET /api/v1/admin/content/trash?type=` lists deleted items; `POST .../items/{type}/{slug}/restore` brings one back **as a draft** (restoring never publishes by itself).

## Preview tokens
`POST .../items/{type}/{slug}/preview-token` `{ "ttlMinutes": 30 }` (content.update; default 60, max 7 days) returns `{ token, expiresAt, path }`. The token is `base64url(itemId|expiry).base64url(HMAC-SHA256)`: it names one item, expires, and cannot be altered or moved to another item. `GET /api/v1/content/preview/{token}?lang=` returns that item in any status (not deleted), `Cache-Control: no-store`, `X-Robots-Tag: noindex, nofollow`; anything invalid, altered, expired or deleted is a plain 404.
Key: `Preview:SigningKey` (≥16 chars). Development generates an ephemeral key (links die on restart); other environments without it get `503 PREVIEW_NOT_CONFIGURED` and accept no tokens. A leaked link works until it expires, so keep lifetimes short.
