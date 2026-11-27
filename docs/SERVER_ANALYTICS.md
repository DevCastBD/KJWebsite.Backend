# Server-side analytics and funnels (BE-090, #92)

Collected by `ReportingService` at `POST /api/v1/analytics/events` (gateway route `analytics`, anonymous). It is the collector the frontend already posts to (frontend `docs/ANALYTICS_EVENTS.md`, issues #27 and #108): body `{name, props?, path?}` as JSON, sent as `text/plain` so the browser needs no preflight, no cookies, no credentials. The response is 202 with no body.

## Event names (a contract: add, never rename)
`application_page_view`, `application_started`, `registration_submit_success`, `application_step_view`, `application_step_complete`, `application_abandon` (steps `personal`, `education`, `address`, `motivation`, `attachments`, `review`) and `donate_view`, `donate_copy` (prop `field`), `donation_step_view`, `donation_step_complete`, `donation_abandon` (steps `amount`, `details`, `review`). Step events need a step from their funnel's list; other events take none. An unknown name is 400. `ReportingService/Analytics/AnalyticsStore.cs` holds the list and a test pins it.

## Privacy
No personal data is stored: props are limited to `step`, `field` and `run` and each must be a token of up to 40 letters, digits, `.`, `-`, `_` (an email, a name or a sentence is a 400). `path` is kept only if it is a plain path. The IP is never stored: the `visitor` column is a keyed hash of IP, user agent and the day (`Reporting:Analytics:HashSecret`; the day in the input means it cannot be followed across days). No cookie is set. `DNT: 1` and `Sec-GPC: 1` get 202 and nothing is stored. A hashed visitor is limited to `MaxEventsPerMinute` (120, then 429) and a body to 2 KB (413). Events older than `RetentionDays` (730) are deleted by the nightly job; the final window follows the retention decision (#110).

## Funnels
`GET /api/v1/admin/reporting/funnels/{application|donation}?from=&to=` (`reporting.view`, default last 30 days, at most 366) returns, per step, distinct visits that `entered`, `completed` and `abandoned` it with `dropOffPercent`, plus `visits`, `converted` (completed the last step), `abandoned`, `inProgress`, `conversionPercent` and the count of each event name. A visit is one `run`. Abandonment is reported as such, not inferred from missing completions.
