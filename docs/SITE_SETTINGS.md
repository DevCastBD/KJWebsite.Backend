# Site settings and runtime flags (BE-037, #45)

Editable without a developer or a deployment. Stored in ContentService (`settings`, `feature_flags`), audited, cached and evicted on every change.

## Settings
`PUT /api/v1/admin/content/settings/{key}` body `{ "value": ..., "isSecret": false }` (`settings.manage`; reads need `settings.view`; honours `If-Match`).

| Key | Shape |
|---|---|
| `navigation.main`, `navigation.footer` | list of `{ id, label:{bn,en}, url, children:[…] }` (one sub-level, unique ids, url starts with `/`, `http://` or `https://`) |
| `contact` | `{ address:{bn,en}, email, phone?, mapUrl? }` |
| `social` | list of `{ network, url }` |
| `footer.text` | `{ bn, en }` |
| `homepage.sections` | ordered list of distinct section ids |
| `maintenance` | `{ enabled, message:{bn,en} }` (message required when enabled) |
| `custom.<name>` | one `{ bn, en }` text, may be `isSecret` |

**Both languages are required** on every visible text, or the save is a 400 and nothing is stored. Catalog settings are public by design and cannot be secret.

## Public
`GET /api/v1/content/settings?lang=bn|en` returns `{ lang, settings, flags }` with every text resolved to one language; `bootstrap.settings` carries the same. Responses are output-cached and evicted on any settings change, so edits show immediately. **Secret settings are never in any public response.** Secret values are hidden in the admin list and shown only by `GET .../settings/{key}` to `settings.view` holders. They are stored as plain database values (not encrypted); do not put production credentials here until the secrets store from #36 is wired in.

## Maintenance mode
Reported, never enforced. The settings endpoint always answers 200 with `settings.maintenance`, and content endpoints stay up, so the frontend decides what to show and keeps its static navigation if this API is unreachable. Delete a setting to fall back to the frontend's static value (F2-17).

## Runtime flags vs. frontend build flags
Runtime flags (`/api/v1/admin/content/flags/{key}`, public under `flags`) are switched in the back office and take effect at once. They are **not** the frontend's build-time `VITE_ENABLE_*` capability flags, which are fixed at build. Keep the names different (runtime keys are lowercase dotted/hyphenated, for example `new-donate-page`).
