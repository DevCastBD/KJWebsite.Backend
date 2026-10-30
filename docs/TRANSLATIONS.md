# Translations: completeness and fallback (BE-035, #40)

Bengali (`bn`) is the primary language and English (`en`) the secondary one. Every content type is translated per language; this page says what "translated" means, how to see the gaps, and what a visitor sees meanwhile.

## What counts as complete
A language is **complete** when every required text field is filled (not blank). Required fields per type:

| Type | Required in each language |
|---|---|
| project, news | title, summary, body (projects: title, short description, full description) |
| page, faq (question/answer), testimonial (author/quote) | title, body |
| team-member (name/role), press | title, summary |
| partner | title |

An item is complete when both `bn` and `en` are. `GET /api/v1/admin/content/items/{type}/{slug}/completeness` returns, per language, `present`, `complete`, `percent` and the `missing` fields; the same block is `completeness` in every admin item view (what the side-by-side editor shows).

## The gaps report
`GET /api/v1/admin/content/translations/report` (`content.view`) lists every item with a gap across **all eight types**: per language `missing` (no translation at all) or `incomplete` (fields blank). Filters: `type`, `lang`, `status`, `includeComplete`, `limit`, `offset`. The response also carries `byType` and `byLanguage` counts for the whole filtered set, so a dashboard can show the backlog at a glance.

## Going live with a gap
Publishing or scheduling an incomplete item succeeds but returns `warnings` (for example `bn translation is missing`). Set `Content:Translations:RequireAllLanguages=true` to make it a 400 instead. Sending to review is never blocked.

## What a visitor sees (decided)
When the visitor's language has **no translation row** for an item, the public API follows a rule per requested language (`Content:Translations:Fallback:bn` / `:en`; values `hide`, `fallback`, `placeholder`):

| Requested | Default | Result |
|---|---|---|
| `bn` | `placeholder` | The item is listed with the English title, empty summary/body, `translationPending: true`, `requestedLang: "bn"`. The site shows "translation pending", never silent English text. |
| `en` | `fallback` | The Bengali content is served in full, flagged `translationPending: true`. |
| either | `hide` | The item is absent in that language (the behaviour before this change). |

`lang` on the item is always the language of the text actually returned. An unknown setting stops the service at startup. Applies to projects, news and the six generic types, including their related items; admin endpoints always return every translation. Only the fields `translationPending` and `requestedLang` were added to the public contract.
Note a blank field inside an existing translation is *incomplete*, not missing, and is served as written.
