# Configurable impact metrics (BE-085, #89)

Lives in `ReportingService` under `/api/v1/admin/reporting/metrics` (`reporting.view` to read, `reporting.manage` to change). A metric is a **definition** over a warehouse metric (#94), not code.

A definition has a `key`, `name`, `unit`, plain-language `methodology` (the note to show beside the figure), the warehouse `metric` it counts, a `kind` (`monthly` adds up over months; `snapshot` is a state at a point in time and is never summed) and a `scale` (for example 1/60 turns approved minutes into hours). Seeded: `students-reached` (unique students currently enrolled, counted once), `student-attendances` (headcount per session, a student attending ten sessions counts ten times), `sessions-delivered`, `institutions-engaged`, `volunteer-hours` (approved hours only). The organisation still has to decide which of these it publishes and substantiates; changing what `students-reached` means is a new version, not an edit.

## Versions never rewrite history
Definitions are append-only dated versions. `POST /metrics/{key}/versions` adds one that applies from its `effectiveFrom` month onward (default: this month); it cannot start earlier (422 `HISTORY_LOCKED`) and must restate the methodology. Every month of a series is computed under the version in force for that month and each point reports its `definitionVersion`, so a changed definition leaves earlier months exactly as published. Changes are audited (`/api/v1/admin/reporting/audit`).

## Series
`GET /metrics/{key}/series?project=&from=&to=`: organisation-wide, or one project (the warehouse dimension is the project slug). The response carries the definition and all versions, `trace` (warehouse metric, source and the call that returns its rows), `asOf`, `stale`, and `total` for monthly metrics. Volunteer hours count only approved records and session counts come from logged sessions, so every figure traces to approved records in Membership.
