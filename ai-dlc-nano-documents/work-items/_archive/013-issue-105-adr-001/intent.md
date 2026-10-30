# ADR-001 — modular monolith or microservices
- Source: issue #105 https://github.com/DevCastBD/KJWebsite.Backend/issues/105
- Type: feature
## Request
- Decide whether P1 consolidates the current four services into a modular monolith behind YARP or keeps separate microservices.
- Record the chosen design, rejected alternative, reasoning, and consequences in `docs/adr/0001-*.md` before #12 ratifies the ADR set or #21 writes migrations.
## Decisions
- True microservices chosen by the user on 2026-10-12; retain independently deployed services and add service-to-service auth, service discovery, and distributed tracing to P1 scope.
- Rationale: the applications may later need to be deployed separately under different domains or hosted together; retain flexibility for either arrangement.
- Reject the proposed modular monolith despite its lower operating cost; document the trade-off.
- Ratified on 2026-10-12 by Md Tahmidul Abedin (@tahmidsparrow), confirmed by the user.
## Out of scope
- Implementing the topology change or PostgreSQL migration; those belong to later P1 issues.
## Follow-ups
- None yet.
