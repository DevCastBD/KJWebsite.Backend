# Retention, consent records, export and erasure (BE-052, #58; closes G30)
- Source: issue #58 https://github.com/DevCastBD/KJWebsite.Backend/issues/58 (milestone 4 / P3); #110 (windows) skipped by user, so windows are configuration with no defaults
- Docs: ADR-014 (no data kept indefinitely, windows are configuration, audit keeps only a non-identifying record after erasure, field-level encryption); issue notes (purge audited: what, when, which policy)
- Type: feature
## Request
- Configurable retention for rejected/withdrawn/postponed applications; automatic purge, audited; unset window = never purged
- Consent records: who, what, when (incl. consent to process an application)
- Export and erasure end to end, audited; erasure also removes the Identity account
- Public privacy-behaviour endpoint so the site's statement can show the real windows
## Decisions
- (approved) audit log after erasure: stop personal data entering audit rows (ignore lists for Member, Identity user/profile) and, at erasure, null only ActorEmail/Ip/UserAgent on the subject's own audit rows through a narrow PostgreSQL trigger exception; everything else stays immutable
- (approved) applicants (not members): export returned on reference+email (rate limited); erasure is a request that staff execute
- (approved) purge runs as a hosted worker (#33 not on develop) plus an admin run/dry-run endpoint; no default windows
## Out of scope
- Donor records and analytics retention (P4/#111), backups (operational: must age out within the published window), legal minimums (#114)
## Follow-ups
