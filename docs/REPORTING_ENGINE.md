# Reporting engine (BE-086, #90)

In `ReportingService`, under `/api/v1/admin/reporting`. Reports read the warehouse and the impact metric definitions only, so a report never disagrees with a dashboard.

## Saved reports and runs
`POST /reports {name, template, parameters?}` (`reporting.manage`) saves a definition. Templates (`GET /report-templates`): `impact-summary` (every metric by month with its definition and version), `financial-summary` (donations, ledger expenses, budget) and `aggregate-series` (one warehouse metric, parameter `metric`). Parameters: `month` (2026-03), or `from` and `to`, or `months` (1-60). `POST /reports/{id}/runs {parameters?, formats?}` (`reporting.view`) runs it now: parameters override the saved ones, formats are any of `csv`, `xlsx`, `pdf` (default all). Each export is generated once and stored; `GET /report-runs/{runId}/files/{format}` returns the same bytes every time.

Exports keep Bengali intact: CSV is UTF-8 with a byte-order mark (a report with several tables is a zip of one CSV per table), XLSX stores text as UTF-8 strings, PDF embeds Noto Sans Bengali (SIL OFL). Tests check all three.

## Financial reports
`financial-summary`, and `aggregate-series` over a finance metric, are sensitive: saving, listing, running and downloading them also needs `finance.view` (they are absent from the list otherwise). Run parameters cannot turn a general report into a financial one. Recipients of a financial schedule are added and removed only with `reporting.recipients` (plus `reporting.manage`). Addresses are stored encrypted (Data Protection; set `DataProtection:KeysPath` in deployment) and kept out of the audit log values; adds and removals are audited (`/api/v1/admin/reporting/audit?entity=ReportRecipient`), and a removal keeps the row.

## Scheduled delivery
`POST /reports/{id}/schedules {dayOfMonth 1-28, hourUtc, formats?, parameters?}` mails the exports every month; with no window parameter the run covers the previous calendar month. The worker checks every `Reporting:Reports:TickSeconds` (60). A schedule stays due until a run succeeds: generation is tried `Attempts` times, a failed generation or send is logged at Error as `ALERT: ...`, recorded (`failureCount`, failed runs and deliveries) and tried again after `RetryMinutes` (60). A retry reuses the files already generated and mails only the recipients who have not received them, so nothing is skipped and nobody gets two. Archiving a report stops its schedules.

Mail goes through `Reporting:Smtp` (`Host`, `Port`, `UseSsl`, `User`, `Password`, `From`). With no host configured every send is refused (`MAIL_NOT_CONFIGURED`), so a schedule fails loudly instead of looking delivered.
