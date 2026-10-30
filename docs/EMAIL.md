# Email delivery

Provider-agnostic `IEmailSender` in `BuildingBlocks/Email` (ADR-007: Postmark primary, SendGrid/SES swap in behind the same interface). Messages are queued through the transactional outbox ([OUTBOX.md](OUTBOX.md)) and sent by the background worker; a request never waits for, or fails because of, the mail provider.

## What is sent

A submission to `POST /api/v1/cta/submissions` queues, in the same transaction as the submission:

| Email | To | When | Template |
|---|---|---|---|
| Acknowledgement | the sender | `values.email` is a valid address | `ContactAck`; `RegistrationReceived` when `formName` is `registration` |
| Notification | `Email:OrgInbox` | an inbox is configured | `AdminNotification` (reference, form, name, email, phone, message) |

The sender's mail is in the form's `language` (`bn` or `en`; the API default is Bengali on the frontend). The org notice uses `Email:OrgLanguage` (default `bn`). Templates are in `CtaSubmissionService/Email/CtaEmailTemplates.cs`: every string exists in both languages, values are HTML-escaped, and the layout is a 600 px table with inline styles.

Outbox payloads carry only the submission id and email kind; the handler reads the personal data at send time. Delivery is at-least-once, so a crash between send and commit can send one email twice.

## Configuration

| Setting | Notes |
|---|---|
| `Email:Provider` | `Postmark` or `File`. Default: `File` in Development, `Postmark` elsewhere |
| `Email:FromAddress`, `Email:FromName` | sender identity on the verified domain |
| `Email:Postmark:ServerToken` | secret: `Email__Postmark__ServerToken` env var or user-secrets, never committed |
| `Email:Postmark:MessageStream` | default `outbound` |
| `Email:OrgInbox`, `Email:OrgLanguage` | organisation notification address and language |
| `Email:File:Directory` | Development sink: each mail is written as `.txt` + `.html` (default `$TMPDIR/kj-mail`) |

Outside Development the service refuses to start without the token and from-address. Locally nothing leaves the machine: open the `.html` files from the sink, or point a tool such as `smtp4dev` at a custom `IEmailSender`.

## Not covered by code (manual)

- **SPF, DKIM, DMARC** on the sending domain: blocked on who controls DNS (#107). Until they pass, production mail will land in spam. Verify with the provider's domain check and a mail-tester message.
- **Client rendering** in Gmail, Outlook and a mobile client: send the templates once through Postmark and look at them.
- Account emails (email confirmation, password reset) still use the in-memory sender in `AuthIdentityService`; moving them onto this sender needs an outbox in the Auth database and is tracked in the backlog.
