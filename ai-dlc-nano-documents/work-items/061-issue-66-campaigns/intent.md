# Campaigns with goals and cached public progress (BE-061)
- Source: issue #66 https://github.com/DevCastBD/KJWebsite.Backend/issues/66 (milestone 5, P4)
- Docs: docs/DONATIONS.md, docs/LEDGER.md; FE contracts GET /api/v1/campaigns/{slug}/progress and admin /campaigns (src/api/endpoints/campaignProgress.ts, src/admin/finance/api.ts)
- Type: feature
## Request
- Campaigns with goal, deadline, status; public progress endpoint that is cached and survives a spike; totals reconcile with the ledger and never exceed what settled; documented multi-currency rule.
## Decisions
- Currency rule: headline `raised` counts only gifts in the campaign's goal currency; other currencies are published as separate per-currency amounts (`otherCurrencies`), never converted (user)
- Assumed: raised = valid (confirmed, not voided) receipts designated to the campaign, so pending and reversed money never counts; progress is computed once per TTL (single-flight, stale-while-error) and served with Cache-Control + ETag; cache TTL via config (default 30s) with optional shared output cache like Content; a reconcile endpoint compares cached vs recomputed vs ledger income; donations designated to a campaign now require an existing, accepting (active, within dates) campaign; offline gifts require it to exist and not be draft; slug from title or given, immutable; admin routes match the frontend (GET|POST /campaigns, PUT /campaigns/{id}; body title, goal, currency, status, endsOn); campaign rows mutable and audited
## Out of scope
- Redis deployment, campaign pages/content, projects designation validation (Content service)
## Follow-ups
- Progress cache is per instance; a shared (Redis) output cache in front of the gateway is needed for multi-instance consistency (#48 Redis exists for Content/idempotency)
- Frontend may want `otherCurrencies` rendered (additive field); campaign pages/content live in the Content service
- OpenAPI names: Finance campaign schemas are prefixed Fundraising to avoid Membership's recruitment campaigns
