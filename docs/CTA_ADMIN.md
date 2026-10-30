# CTA submissions: admin API and anti-spam

Submissions carry personal data (names, phones, addresses, blood groups). Every endpoint below needs a permission, writes an audit row naming who did it, and keeps the data out of logs (see [OBSERVABILITY.md](OBSERVABILITY.md)).

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /api/v1/admin/cta/submissions` | `cta.view` | paged (`limit` ≤ 100, `offset`), newest first |
| `GET /api/v1/admin/cta/submissions/{id}` | `cta.view` | full values plus the update history |
| `PATCH /api/v1/admin/cta/submissions/{id}` | `cta.manage` | `status`, `assigneeId`, `notes`; omitted = unchanged, empty string clears |
| `GET /api/v1/admin/cta/submissions/export` | `cta.export` | CSV, same filters, at most 5000 rows |

Filters: `status`, `formName`, `ctaType`, `language`, `assigneeId`, `q` (id, name or any value, case-insensitive, `%` and `_` literal), `from`, `to` (on `createdAt`). Statuses: `new`, `in_review`, `contacted`, `resolved`, `spam`, `archived`.

`cta.manage` is held by Admin, SuperAdmin and Moderator; `cta.export` by Admin and SuperAdmin (an export is a bulk copy of personal data). Roles re-sync from `KjRoles.Seed` when the Auth service starts.

## CSV

UTF-8 **with a byte-order mark** so Excel shows Bengali instead of mojibake, CRLF rows, RFC 4180 quoting. Cells that Excel would execute (`=`, `@`, tab, CR, or `+`/`-` that is not a plain number) get a leading `'`; phone numbers such as `+8801712345678` are left alone. Columns: id, form, type, language, status, assignee, timestamps, name, email, phone, message, notes, and the raw `values` JSON.

## Audit

`submission_audit` records actor (token subject), action (`list`, `view`, `export`, `update`), the submission id and a short detail: filter names (never search text), and for updates the status/assignee change. Notes are recorded as "notes changed", never their content.

## Anti-spam (public `POST /api/v1/cta/submissions`)

All additive; the static frontend keeps working unchanged.

- **Honeypot.** A hidden field named `website` (configurable: `AntiSpam:HoneypotField`) inside `values`. Any non-empty value rejects the submission.
- **Timing.** Optional `formOpenedAt` (ISO timestamp, the moment the form was shown). `submittedAt - formOpenedAt` below `AntiSpam:MinFillSeconds` (3) rejects it. Both values come from the client's clock, so skew cancels. A client that does not send it is not checked.
- **Turnstile (optional).** Set `AntiSpam:Turnstile:SecretKey` and clients must send `turnstileToken`. Off by default. If Cloudflare is unreachable the check fails open, so the filter cannot take the volunteer form down.

A rejected submission is not stored and gets `400` `SUBMISSION_REJECTED` with one generic message for every reason, so a bot cannot tell which check caught it. Rate limiting (#28) sits in front of all of this.

Frontend follow-ups: render the hidden `website` input, send `formOpenedAt`, and (if Turnstile is enabled) send `turnstileToken`.
