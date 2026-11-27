# Press coverage (BE-080, #84)

`press` is a generic content type (`/api/v1/content/press`, `/api/v1/admin/content/press`): headline in `title`, excerpt in `summary`, with translations, the editorial workflow, revisions and redirects like every other type. This adds the structured part in `press_coverage` (1:1 with the item).

## Coverage details
`GET|PUT /api/v1/admin/content/press/{slug}/coverage` (`content.view` / `content.update`, `ETag`/`If-Match` like the event schedule):

| Field | Notes |
|---|---|
| `outlet` | Required. |
| `outletLogoUrl` | https URL or site path, optional. |
| `publishedOn` | Date of the coverage. |
| `externalUrl` | Required, https. Linking is always safe. |
| `clippingUrl` | Hosted scan (image or PDF). Accepted only when `reproductionPermission` is `granted` and `permissionNote` says who agreed and how. |
| `language` | Language of the coverage itself (`bn`, `en`), separate from the languages of the item's text. |
| `reproductionPermission` | `unknown` (default), `linked-only`, `pending`, `granted`. |
| `featured`, `featuredOrder` | Homepage strip. |

The public API never returns a clipping unless permission is `granted` (so withdrawing permission hides it at once, link kept) and never returns `permissionNote`.

## Public reads
- `GET /api/v1/content/press/{slug}/coverage`: one published item.
- `GET /api/v1/content/press-coverage?lang=&coverageLanguage=&featured=&limit=&offset=`: newest first; `featured=true` gives the homepage strip in editor order. Items without coverage details, or without a translation in `lang`, are left out.

## Featured ordering
`PUT /api/v1/admin/content/press-featured-order {slugs: [...]}` (`content.publish`): the listed items become featured at their position, all others are un-featured. Takes effect on the next public read, no deployment.

## Not done here
Large clipping scans are not compressed: there is no media pipeline (#41) in this codebase yet, so logo and clipping are URLs to wherever the file is hosted. When the pipeline lands, point these fields at its variants.
