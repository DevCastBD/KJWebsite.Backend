# B0-5 Docker Compose
- Source: issue #10 https://github.com/DevCastBD/KJWebsite.Backend/issues/10
- Type: feature
## Request
- Add a multi-stage Dockerfile for each service without the .NET SDK in runtime images.
- Add Compose for all services, PostgreSQL, Redis, and MailHog.
- Make health endpoints and Scalar API reference reachable through the container stack.
## Decisions
- Use one PostgreSQL container with persistent data and development-only credentials; initialize separate `auth` and `cta` databases because each context uses `EnsureCreated()`.
- Run containers in Development, expose ports 7000–7003, and verify `/health` plus the gateway Scalar reference.
- Add four multi-stage Dockerfiles, Compose, `.dockerignore`, configurable gateway destinations, and README instructions.
- Redis and MailHog are declared only; application integration is deferred.
## Out of scope
- Application migrations from SQLite to PostgreSQL and production deployment configuration.
## Follow-ups
- none
