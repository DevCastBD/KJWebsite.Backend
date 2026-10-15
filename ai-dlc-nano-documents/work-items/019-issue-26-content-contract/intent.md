# Content contract + /content/bootstrap (BE-006)
- Source: issue #26 (closes G25; #20 documents it)
- Type: feature
## Request
- Public reads return { items, total, limit, offset, lang } (lang = resolved)
- GET /api/v1/content/bootstrap?lang= → projects, news, settings in one call
- Cache-Control + ETag on public reads; admin reads return all translations
- Update openapi.v1.yaml via tools/OpenApiExporter
## Decisions
- Build on in-memory data now; #25 (persistence) swaps source later → confirmed
- Bootstrap: { projects:{items,total,limit,offset}, news:{...}, settings:{}, lang } → confirmed
- Projects get limit/offset (default 20, max 100) like news → confirmed
- Admin GET list + by id, all translations grouped per slug → confirmed
## Out of scope
- Persistence (#25), real settings store, frontend adapter
## Follow-ups
- Bootstrap settings is empty {} until a settings store exists
- #25 must keep these envelopes when swapping to EF
