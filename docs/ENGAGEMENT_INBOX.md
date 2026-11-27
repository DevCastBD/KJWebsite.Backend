# Unified engagement inbox (BE-083, #87)

Lives in CtaSubmissionService (`cta` schema); every form that posts to `POST /api/v1/cta/submissions` lands here. The back-office routes are under `/api/v1/admin/cta` (gateway route `admin-cta`): `cta.view` to read, `cta.manage` to change.

The back-office handling the issue builds on (#46) did not exist in this codebase, so assign, respond and close are part of this change.

## Categories
Set at intake from `ctaType`: `join` is `membership`, `get-involved` is `volunteering`, `partnership`, `donate` is `donation`, `media` (new accepted ctaType for media enquiries) and everything else `general`. A routing rule may change the category.

## Routing rules
`PUT /routing-rules {rules:[...]}` replaces the ordered list; the first active matching rule wins. A rule matches on `matchCategory`, `matchLanguage` and/or `matchKeyword` (text found in the submitted values; at least one is required so a rule cannot swallow everything) and sets `assignTo` (a user id), `priority` (`urgent`, `high`, `normal`) and/or `setCategory`. Rules are data, not code, so the organisation can restructure without a deployment. They apply to enquiries received afterwards. **Media enquiries are never below `high`**, with or without a rule.

## Inbox
`GET /inbox?status=&category=&assignee=&slaState=` (`assignee`: a user id, `me`, `none`; `status`: new, in_progress, responded, closed, or `open`): urgent first, then by response deadline. `GET /inbox/{id}` shows the values and history. `POST /inbox/{id}/assign {assignee}` (`me` allowed), `/respond {note}` (records that someone answered outside this system; the first one stops the clock), `/close {note}`. Every step is in the enquiry's append-only history. Sorting and paging happen in memory over the matching rows, which is fine at volunteer-organisation volumes.

## SLA
`PUT /sla-targets {targets:{media:240, general:null}}` sets first-response targets in minutes per category (null removes one). **No targets exist until the core members agree them**: with none, `slaState` is `none` and nothing is timed; this change invents no numbers (frontend D-16 is the open question of who owns it). The clock runs from when the service received the enquiry (the client's `submittedAt` is not trusted). State is always computed from current targets: `on_track`, `at_risk` (a quarter or less of the target left), `breached`, `met`, `met_late`, `closed_unanswered`, `none`. Filter `slaState=at_risk` to see problems before they breach.

`GET /sla-report?from=&to=` (default last 30 days): per category, received, responded, closed unanswered, open, open breached, open at risk, answered late, average and median first-response minutes, and the target.

## Not done
No email or in-app alert is sent when an enquiry becomes at risk or breached (notifications and delivery live in ContentService); today the signal is the inbox filter and the report.
