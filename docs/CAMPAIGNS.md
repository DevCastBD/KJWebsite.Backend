# Campaigns and public progress (BE-061, #66)

## Campaigns
A campaign has a title (and optional Bengali title), a **goal in one currency**, a status (`draft`, `active`, `ended`), optional start and end dates, and a **slug**: its public identifier, made from the title (or given), unique, and never changed. Back office, matching the frontend: `GET|POST /api/v1/admin/finance/campaigns` and `PUT .../campaigns/{id}` with `{title, titleBn?, slug?, goal, currency, status?, startsOn?, endsOn?}` (`finance.campaign.manage` to write, `finance.view` to read). The currency cannot change once any gift is designated to the campaign (`CURRENCY_LOCKED`). Edits are audited. A campaign takes public gifts while it is `active`, has started and has not passed its end date.

## Designating gifts
A public donation that names `designation: {type: "campaign", id: slug}` must name a campaign that is open to gifts today, otherwise `400 designation.id`. A gift recorded in the back office (offline) needs a campaign that exists and is not a draft; it may have ended since the gift arrived. `project` designations stay opaque slugs (projects belong to the Content service).

## The public figure
`GET /api/v1/campaigns/{slug}/progress` -> `{raised, goal, currency, asOf, cacheSeconds, gifts, otherCurrencies}` (decimal strings), 404 for an unknown or draft campaign.

**Rule for several currencies.** `raised` counts only gifts **in the campaign's own currency**, so it is never inflated by an exchange rate nobody agreed. Gifts in other currencies are published beside it in `otherCurrencies` as their own amounts, **never converted** into the headline (the rule from the donation domain: nothing is converted silently). In-kind gifts are not in `raised` (admin sees them separately). Only **confirmed, un-voided receipts** count, so a pending, failed or reversed gift never appears, and the figure can never be higher than what has actually been confirmed; anonymous gifts count (as an amount) without being attributable.

**Caching.** The figure is computed at most once per campaign per `Finance:Campaigns:ProgressCacheSeconds` (default 30) however many requests arrive: simultaneous callers share one in-flight computation, so a press-mention spike costs one query per interval. Responses carry `Cache-Control: public, max-age=<remaining>` and an `ETag` that changes only when the figures do (`If-None-Match` gets 304). If a refresh fails, the previous figure is served for up to `StaleMultiple` (10) intervals; beyond that the endpoint answers **503 + Retry-After** instead of an old number, and the frontend keeps its own snapshot. Editing a campaign refreshes it at once. The cache is per instance; for several instances put the gateway's shared output cache in front (Redis, as for Content) or accept up to one interval of difference.

## Reconciliation
`GET /api/v1/admin/finance/campaigns/reconciliation` compares, per campaign and currency, the receipts' total with the net designated-donation income the ledger holds for the same donations (reversals netted), reports the difference (zero when the books agree), and shows what the public cache holds for the campaign's own currency right now (it can lag by one interval and never exceeds the receipts).
