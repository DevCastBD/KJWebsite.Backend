<!-- phase: CONSTRUCT | branch: 10-feature-b0-5-add-a-dockerfile-per-service-and-a-docker-composeyml | tasks: 4/4
     base: 1bd3653 | updated: 2026-09-20
     next: run Compose startup and health/Scalar probes in a Docker-enabled environment -->
# Plan: B0-5 Docker Compose
## Tasks
- [x] Add a root `.dockerignore` and a multi-stage, non-SDK-runtime Dockerfile for each service.
- [x] Make gateway destinations configurable while retaining local-development defaults.
- [x] Add Compose and PostgreSQL initialization for the four services, PostgreSQL, Redis, and MailHog; wire ports, separate development databases, startup order, and persistent data.
- [x] Document one-command startup and health/Scalar verification in the README.
## Tests
- Standard: run `dotnet test KJWebsite.Backend.slnx` and `dotnet format KJWebsite.Backend.slnx --verify-no-changes --no-restore`; run `docker compose config` and `docker compose up --build`, then probe each health endpoint and gateway Scalar. Docker is unavailable in this workspace, so container commands require a Docker-enabled environment.
- Results: build, formatter, unit tests, and static YAML/Dockerfile checks pass. Integration tests and runtime probes are blocked by unavailable Docker and loopback sockets.
## Files
- `.dockerignore`, `docker-compose.yml`, `docker/postgres/init-databases.sql`, `src/Services/*/Dockerfile`, `src/Services/ApiGateway/Program.cs`, `README.md`
