# Content model, taxonomy and redirects (BE-031, #38)

## One model, eight types
Every kind of content is a `content_items` row with a `Type`: `project`, `news`, `page`, `faq`, `team-member`, `testimonial`, `partner`, `press`. Each language is a `content_translations` row (title, summary, body, free-form string `attributes`). Projects and news keep their extension tables and dedicated endpoints; the other six use the generic API below, which has the same workflow (draft / published / archived, soft delete with the slug reserved, ETag/If-Match, cache eviction).

| Type | Public path segment / API segment |
|---|---|
| project, news | `projects`, `news` (dedicated endpoints, unchanged) |
| page, faq, team-member, testimonial, partner, press | `pages`, `faqs`, `team-members`, `testimonials`, `partners`, `press` |

Public: `GET /api/v1/content/{segment}` (`lang`, `tag`, `limit`, `offset`) and `/{segment}/{slug}`; `GET /api/v1/content/items/{type}/{slug}` serves **all eight** in the generic shape. Only published items with a translation in the requested language are listed.
Admin (`content.*` permissions): `GET/POST /api/v1/admin/content/{segment}`, `GET/PUT/DELETE .../{slug}`. POST creates or restores; PUT replaces translations, tags and related items.

## Tags
One `Tag` per concept (slug) with a name per language (`tag_names`). `(lang, normalised name)` is unique, so "শিক্ষা" and "শিক্ষা " cannot be two tags and a bn name cannot shadow an en one. Normalisation (`TagNormalizer`): Unicode NFC (so precomposed য়/ড়/ঢ় equal their decomposed forms), zero-width characters removed, Bengali digits to ASCII, case-folded, whitespace collapsed, leading `#` dropped.
Items refer to tags by slug **or** by a name in any language; unknown tags are a 400 (never auto-created, to keep the taxonomy deliberate). `PUT /api/v1/admin/content/items/{type}/{slug}/tags` works for all eight types; tag CRUD is under `/api/v1/admin/content/tags`; public `GET /api/v1/content/tags?lang=`.

## Related content
`PUT /api/v1/admin/content/items/{type}/{slug}/related` takes `[{type, slug}]` of any type, order kept. Public detail returns each related item's `type`, `slug` and `title` in the requested language; drafts, deleted items and items without that translation are omitted.

## Redirects
`redirects(from_path unique, to_path, status_code, source)`. Paths are normalised (lower case, leading slash, no query/fragment/trailing slash) and rows are **flattened on write**: resolving is one lookup, adding A→B retargets anything pointing at A and follows B's own redirect; loops are rejected.
- `POST /api/v1/admin/content/items/{type}/{slug}/rename` changes the slug and writes `/{segment}/{old}` → `/{segment}/{new}` (301, source `slug-change`) in the same transaction. Renaming back to an old slug removes the redirect that would shadow it.
- Legacy `.html`/`.php` URLs and the frontend's language-prefix moves are manual rows: `POST/GET /api/v1/admin/content/redirects`, `DELETE .../{id}`.
- Public `GET /api/v1/content/redirects/resolve?path=` returns `{from, to, statusCode}` or 404. The frontend (or an edge rule) calls it on a 404 and issues the redirect.
- The public path convention assumes the site serves `/{segment}/{slug}` (e.g. `/pages/about`).
