# Events (BE-069, #73)

`event` is a content type (`/api/v1/content/events`, `/api/v1/admin/content/events`) on the generic content API, so it has the editorial workflow, bilingual translations (title, summary, body required), tags, related items, revisions and redirects. Venue name and address are free-form translation `attributes` (e.g. `venueName`, `venueAddress`).

The schedule is the part that is not text, kept in `events` (1:1) and `event_sessions`:

| Field | Notes |
|---|---|
| `start`, `end` | Stored as UTC instants; `end` must be after `start`. |
| `timeZoneId` | IANA id (`Asia/Dhaka`). Responses carry `startLocal`/`endLocal` with the zone's offset, so `.ics` generation never guesses. |
| `onlineUrl` | Absolute https URL, optional. `latitude`/`longitude` come as a pair. |
| `capacity` | Seats per occurrence; null is unlimited. Enforced by registration (BE-070). |
| `recurrence` | RRULE subset: `FREQ` DAILY/WEEKLY/MONTHLY, `INTERVAL`, `COUNT` or `UNTIL` (required), `BYDAY` for weekly. Max 366 occurrences. |
| `exceptions` | Local start times (`2026-03-14T10:00`) removed from the series. |
| `sessions` | Agenda rows: `startOffsetMinutes`, `durationMinutes`, `titles` {en,bn}, `speaker`, `room`; stored in start order and shared by every occurrence. |

A series is one record: occurrences are expanded on read, in the event's own zone, so a weekly 10:00 stays 10:00 across DST and editing the rule or an exception never touches per-occurrence rows. A wall-clock time skipped by a DST jump moves forward one hour.

## Endpoints
- `GET/PUT /api/v1/admin/content/events/{slug}/schedule`: `content.view` / `content.update`; the `ETag` is the item version (`If-Match` on PUT, stale is `409 CONFLICT`).
- `GET /api/v1/content/events/{slug}/schedule`: schedule, sessions and occurrences of a published event.
- `GET /api/v1/content/event-occurrences?lang=&from=&to=&limit=&offset=`: published occurrences in a window (default now to +90 days, max 400), soonest first, title in `lang`.

# Registration (BE-070, #74)

Registrations live in the content database beside the events so a seat is decided in one transaction. Anyone can register (no account); a valid bearer token additionally records `memberId`. Name, email and phone are encrypted (`RegistrationPrivacy`), the email is matched by keyed HMAC (`Events:HashKey`, base64 of 32+ bytes, required outside Development), and `retainUntil` is the occurrence end plus 365 days for the retention purge (#58).

| Status | Meaning | Holds a seat |
|---|---|---|
| `confirmed` | Has a seat | yes |
| `pending_payment` | Paid event: seat held until `deadlineAt` (2 h) | yes |
| `offered` | Promoted off the waitlist: must accept before `deadlineAt` (24 h) | yes |
| `waitlisted` | Capacity reached, in order of registration | no |
| `pending_approval` | Approval-required event, waiting for an organiser | no |
| `cancelled` / `rejected` | Closed; `reason` says why (`cancelled`, `expired`, or the organiser's text) | no |
| `no_show` | Confirmed but did not attend (BE-071) | yes |

- **Capacity**: every seat-moving change bumps the event's `RegistrationVersion` (a concurrency token). Two requests for the last seat cannot both save; the loser redoes the work against the new count and is waitlisted.
- **Waitlist**: a freed seat (cancellation, expired hold) is offered to the next person with a deadline; an unanswered offer or unpaid seat is released lazily on the next touch of the event and passed on.
- **Tickets**: `ticketCode` is 128 random bits (26 Crockford base32 characters) and is the QR payload and the credential for `GET /api/v1/content/event-tickets/{code}`, `POST .../accept` and `POST .../cancel`. Responses are `no-store`.
- **Paid events** (`priceMinor` + `currency` on the schedule): a registration is never `confirmed` before payment. `POST /api/v1/admin/content/events/{slug}/registrations/{id}/payment` (`events.manage`) with a payment reference is the only transition out of `pending_payment`; wiring it to the Finance payment confirmation is the follow-up.
- **Notifications** are written to `event_notifications` in the same commit (templates `event.registration.{received,confirmed,waitlisted,pending_payment,rejected,expired}`, `event.waitlist.offer`), recipient and payload encrypted; delivery is the outbox work (#32/#33).
- Admin (`events.manage`): `GET .../events/{slug}/registrations?status=` (with a seat summary), `POST .../registrations/{id}/approve|reject|payment|cancel`.
