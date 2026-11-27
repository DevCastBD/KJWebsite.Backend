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

# Attendance (BE-071, #75)

All admin calls need `events.manage`.

- **Check-in** `POST /api/v1/admin/content/events/{slug}/check-ins` takes `{ items: [{ ticketCode | registrationId, checkedInAt? }] }` (1 to 500). It is built for the organiser app's offline queue: every item is answered (`checked_in`, `already_checked_in`, `not_found`, `wrong_event`, `not_eligible` with the registration status), a repeat is a success, the **earliest** `checkedInAt` wins, a time in the future is clamped to now, and a late scan of someone already marked `no_show` puts them back to `confirmed`. Only `confirmed` (or `no_show`) registrations can check in: unpaid, waitlisted and cancelled ones are `not_eligible`.
- **No-shows** `POST .../attendance/close` (`{ occurrence? }`, default the latest ended occurrence) marks confirmed registrations that never checked in as `no_show`. Cancelled ones stay `cancelled`, so the two are distinguishable; the registrations summary reports `checkedIn`.
- **Certificates** `POST .../registrations/{id}/certificate` (`{ name? }`) issues once, only to someone who checked in; `name` prints a corrected spelling (no re-issue afterwards). The PDF (`GET .../registrations/{id}/certificate`, or for the holder `GET /api/v1/content/event-tickets/{code}/certificate`) embeds Noto Sans Bengali, as the Finance receipts do. The printed name is encrypted at rest.
- **Feedback** `PUT/GET .../feedback-form` defines up to 30 questions (`rating` 1-5, `choice` 2-20 options, `text`) with bilingual prompts and an `isOpen` switch; questions lock once responses exist. Only a **checked-in** ticket holder can read the form (`GET /api/v1/content/event-tickets/{code}/feedback-form`) and submit once (`POST .../feedback`). Results (`GET .../events/{slug}/feedback`) are aggregates plus free-text answers without any identity, readable only with `events.manage`: feedback can be candid, so it is not given to ordinary content roles.

# Calendar feeds (BE-075, #79)

iCalendar (RFC 5545) output from the content service. Every time is written as a UTC instant (`DTSTART:20260314T140000Z`); the event's own IANA zone, DST included, is applied when occurrences are expanded, so a weekly 10:00 in `America/New_York` is 15:00Z before 8 March 2026 and 14:00Z after, and every client shows the right hour in its own zone. Lines are CRLF, folded at 75 octets without splitting a multi-byte (Bengali) character, and text is escaped (`\ \; \, \n`). `lang` (`en` or `bn`, default from `Accept-Language`) picks the title; the location is the translation's `venueName`, `venueAddress`.

| Endpoint | Auth | What |
|---|---|---|
| `GET /api/v1/content/event-feed.ics` | none | All published events as occurrences from 30 days ago onwards, for subscription (`REFRESH-INTERVAL` 6 h, `max-age` 15 min). |
| `GET /api/v1/content/events/{slug}/calendar.ics` | none | One published event, every occurrence. |
| `GET /api/v1/content/event-tickets/{code}/calendar.ics` | ticket code | The invite for a **confirmed** registration (its own occurrence), `no-store`. Waitlisted, unpaid or cancelled tickets get 409. The registration notifications carry this path as `calendarPath`. |
| `POST/DELETE /api/v1/content/calendar-feed/token` | member token | Create (or rotate) / revoke my subscription URL. |
| `GET /api/v1/content/calendar-feed/{token}.ics` | the token | The member's confirmed registrations (those made with their token). |

**A member feed URL is a credential.** The token is 256 random bits (43 base64url characters), shown once and stored only as a SHA-256 hash, never derived from the member id. One token is active per member: creating a new one revokes the old, `DELETE` revokes it, and an unknown or revoked token is a plain 404. The feed is `private, no-store` with `Referrer-Policy: no-referrer`. Because calendar apps put the URL in every request, treat it like a password and rotate it if shared.

Gateway note: the feed routes are under `/api/v1/content`, which the gateway already forwards.
