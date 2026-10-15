# Content contract

How the ContentService read API relates to the frontend's internal shapes. The wire format is defined by [`openapi.v1.yaml`](../openapi.v1.yaml); paging and language rules come from [`MASTER_PLAN.md`](MASTER_PLAN.md) §4.1. This page documents the **backend side** of the mapping; the frontend documents its own side next to its adapter (`src/api/adapters/`).

## Public reads

| Endpoint | Response |
|---|---|
| `GET /api/v1/content/projects` | `{ items, total, limit, offset, lang }` |
| `GET /api/v1/content/news` | `{ items, total, limit, offset, lang }` |
| `GET /api/v1/content/projects/{slug}` | one project in `lang` |
| `GET /api/v1/content/news/{slug}` | one article in `lang` |
| `GET /api/v1/content/bootstrap` | `{ projects: Page, news: Page, settings: {}, lang }` |

- `lang` is `bn` or `en`: the `?lang=` value, else the first supported `Accept-Language`, else `en`. The **resolved** language is echoed in every list and in `/bootstrap`.
- `limit` is 1–100 (default 20) and `offset` is ≥ 0; otherwise `400` Problem Details. Projects default to `status=active` (`status=all` includes the rest).
- `/bootstrap` returns up to 100 active projects and 100 news items, each as a `Page` (`{ items, total, limit, offset, lang }`), plus `settings` (an empty object until a settings store exists).
- Public reads send `ETag`, `Cache-Control: public, max-age=60` and `Vary: Accept-Language`, and answer `If-None-Match` with `304`.

## Admin reads

Authenticated, `Cache-Control: no-store`, and **all translations, never one**:

- `GET /api/v1/admin/content/{projects|news}` → `{ items, total, limit, offset }`, one item per slug.
- `GET /api/v1/admin/content/{projects|news}/{slug}` → `{ slug, …shared fields, translations: [{ lang, …translated fields }] }`.

## Mapping to the frontend

The frontend's internal shape is `{ projects: { bn, en }, news: { bn, en } }` (`contentService.js`). The API is single-language per request, so the adapter builds that shape from two bootstrap calls:

| Frontend field | Source |
|---|---|
| `projects.bn` | `GET /content/bootstrap?lang=bn` → `projects.items` |
| `projects.en` | `GET /content/bootstrap?lang=en` → `projects.items` |
| `news.bn` / `news.en` | same calls → `news.items` |
| active language | `lang` (use the echoed value, not the requested one) |

For lazy loading, call `/bootstrap` once for the active language and the other only when the user switches. Items carry `slug` as their public identifier; ignore `total`/`limit`/`offset` on bootstrap unless `total` exceeds the page size, in which case page through the list endpoints.

## Not yet in place

- Content is in memory and resets on restart (persistence: #25).
- `settings` is empty.
- Generated types and contract tests against the frontend (#37).
