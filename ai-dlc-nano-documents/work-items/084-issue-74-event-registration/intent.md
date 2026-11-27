# Event registration (#74)
- Source: issue #74 (milestone 6)
- Type: feature
## Request
- Guest/member RSVP, capacity+waitlist, approval, paid pending state, QR ticket codes, cancel.
## Decisions
- Registrations live in ContentService (same DB as events) so capacity is decided transactionally (assumed, user chose full run)
