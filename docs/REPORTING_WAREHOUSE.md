# Warehouse-lite: nightly aggregate tables (BE-092, #94)

`ReportingService` (port 7007, schema `reporting`, `ConnectionStrings:ReportingDb`, gateway route `/api/v1/admin/reporting/*`) holds the aggregate tables that dashboards, impact metrics, reports and public figures read. Nothing in P7 queries another service's transactional tables.

## How it fills
Each source service exposes `GET /internal/v1/aggregates` (not routed by the gateway, not in the OpenAPI contract, closed unless the shared key `Internal:Key` is configured and sent as `X-Internal-Key`). It returns monthly points `{metric, period (first of month), dimension, value, currency}` plus `checks`, figures read straight from authoritative records.

| Source | Metrics |
|---|---|
| finance | `donations.confirmed.amount` / `.count` (dimension = designation), `expenses.ledger.amount` (dimension = account code), `budget.planned.amount`; check `ledger.donations.net` |
| membership | `members.joined`, `applications.received`, `volunteer.minutes.approved` (approved only), `sessions.delivered`, `sessions.attendance`, `students.enrolled.snapshot`, `institutions.engaged.snapshot` |
| cta | `inbox.received`, `inbox.first_responded`, `inbox.closed`, `inbox.open.snapshot` |
| content | `content.published`, `content.pipeline.snapshot`, `events.registrations`, `events.attended` |

`*.snapshot` metrics describe the moment the feed was read (period = that month). Points of one metric are disjoint (a dimension split), so a total is the sum.

## Refresh
Nightly at `Reporting:Aggregates:RefreshHourUtc` (default 21, which is 03:00 in Dhaka), or `POST /api/v1/admin/reporting/aggregates/refresh` (`reporting.manage`). A run is all or nothing: every source must answer (retried `Attempts` times) **and** the warehouse's confirmed donation total per currency must equal the ledger's net donation income (tolerance `Tolerance`, default 0.005). If either fails nothing is replaced, the run is stored as `failed` with its reason and logged at Error as `ALERT: ...`, and the last good figures stay but are reported as stale. The next good run clears the alert.

## Reading
`GET /aggregates?metric=...` (`reporting.view`; also `source`, `dimension`, `from`, `to`) returns `items`, `asOf` (the oldest source refresh), `stale` and per-source freshness, so a page can label a figure honestly. A source is stale when never refreshed, older than `MaxAgeHours` (default 30) or when the latest run failed. `GET /aggregates/status` adds `alert`, the last run and the last success; `GET /aggregates/runs` lists runs with their reconciliation lines.

Settings: `Internal:Key` (same value in every service), `Services:{Finance|Membership|Cta|Content}:Url`, `Reporting:Aggregates:*`. Ledger boundary: only `donation` and `reversal` postings to accounts 4010 and 4020 are compared, so manual adjustments to those accounts show up as a disagreement on purpose.
