# Content: distinct 409 codes for slug taken, tag name taken and stale edit (Backend#242)
- Source: issue Backend#242 https://github.com/DevCastBD/KJWebsite.Backend/issues/242 (raised from FE#351 work)
- Type: feature
## Request
- Content answers every 409 with `CONFLICT`; a client cannot tell a taken slug from a stale edit without reading the message.
- Give the cases their own codes so the admin editors can show the right message.
## Decisions
- Stale edit keeps `CONFLICT` (nothing breaks); new codes `SLUG_TAKEN` (as in Membership) and `TAG_NAME_TAKEN` → user chose "Keep CONFLICT".
- POST create refuses any existing slug (live or deleted) with 409 SLUG_TAKEN instead of silently overwriting → user chose "Fix it here".
- Code travels on UpsertResult (`Code`), strings in one ContentErrorCodes class, one response path in ContentHttp.
## Out of scope
- Same-slug create race (loser gets 500), 412 for stale edits, frontend editor changes (FE#381; its `/admin/cms` contract is assumed, not the real `/admin/content`).
- Bulk results stay CONFLICT only: no bulk action changes a slug or tag name, so a passthrough would be dead code (mutation survived, removed).
- Dedicated projects and news POST endpoints keep create-or-replace (legacy, out of this issue).
## Follow-ups
- Projects and news POST still overwrite or revive an existing slug; their Conflict outcome returns 201 with a null body (Program.cs ~263, 309).
- Two creates of one new slug at once (or create racing rename): loser gets 500 DbUpdateException, not SLUG_TAKEN.
- CONTENT_MODEL says a bn tag name cannot shadow an en one, code checks per language only; CONTENT_CONTRACT says slugs never change but rename exists.
- FE#381 premise: the frontend content editor calls an assumed /api/v1/admin/cms contract the backend does not serve.
