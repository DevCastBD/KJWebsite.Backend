# ADR-014: Data residency, privacy and retention

- Status: Accepted
- Date: 2026-10-15
- Ratified by: Fahimul Hoque (@FHShubho)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12), [issue #110](https://github.com/DevCastBD/KJWebsite.Backend/issues/110)

## Context

The platform collects substantial personal data (name, phone, address, blood group, date of birth, education, employer), including from applicants the organisation rejects and has no relationship with. Keeping it indefinitely is the default outcome of not deciding. The concrete retention windows and the data-hosting location depend on core-member decisions still open on #110 and on the organisation's legal status (#114).

## Decision

Principles that bind the design now:

- No personal data is kept indefinitely. Every category (rejected, withdrawn and postponed applications, donor records, analytics events, audit entries) has a retention window held as configuration, not code.
- Rejected applications are purged automatically when their window expires.
- Minimal collection: store only fields a workflow needs. Sensitive profile fields are encrypted at the field level, and personal data never appears in logs.
- Data-subject export and erasure endpoints are provided. The immutable audit log keeps only a non-identifying record after an erasure.
- Personal data of Bangladeshi applicants is stored on infrastructure chosen under the hosting decision (#106, ADR-013), and the chosen location is recorded in the privacy statement.

The numeric windows and the final storage location are set under #110 before P3 starts and recorded here as an amendment. They must match the published privacy statement exactly.

## Rejected alternatives

- Retain everything: unjustifiable, and it enlarges any breach disclosure.
- Hard-code retention periods now: they depend on legal advice (#114), and statutory minimums for financial records differ from those for applications.

## Consequences

- P3 must ship purge jobs, consent records, and export/erasure endpoints.
- Donor-record retention interacts with statutory financial-record minimums and is decided with #114.
- Until #110 closes, this ADR fixes principles but not numbers.
