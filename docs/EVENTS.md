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
