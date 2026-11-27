# Role-aware back-office dashboards (BE-084, #88)

`GET /api/v1/admin/reporting/dashboards?months=12` (gateway route `admin-reporting`, `dashboard.view`) returns the caller's dashboard; `GET .../dashboards/{section}` returns one section.

| Section | Needs | Reads (warehouse metrics) |
|---|---|---|
| `donations` | `finance.view` | `donations.confirmed.amount` / `.count`; optional monthly target line from `Reporting:Dashboards:DonationTargetMonthly:{currency}` |
| `expenses` | `finance.view` | fiscal-year `budget.planned.amount` against `expenses.ledger.amount` (`Reporting:Dashboards:FiscalYearStartMonth`, default 7) |
| `membership` | `membership.manage` | applications received, members joined, conversion |
| `events` | `events.manage` | registrations, check-ins |
| `content` | `content.view` | pipeline by status now, published per month |
| `inbox` | `cta.view` | received, first reply, closed, open by priority |
| `programme` | `programme.manage` | sessions, approved volunteer hours, students and institutions now |

Role-awareness is server-side: a section the caller may not see is not built and not in the response (a content editor's body contains no donation, expense or budget figure). A single section without its permission is 403.

Every figure and series has a `trace` (`metric`, `source`, `drilldown` = the aggregates call that returns its rows). The response carries `asOf` and `stale` from the warehouse. Dashboards read only the warehouse tables (#94), never another service's tables; a test seeds three years across every metric and asserts the full dashboard (36 months, all sections) answers in under two seconds.
