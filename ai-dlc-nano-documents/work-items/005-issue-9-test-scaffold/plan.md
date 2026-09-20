<!-- phase: CONSTRUCT | branch: 9-feature-b0-4-scaffold-test-projects-with-webapplicationfactory-and-testcontainers | tasks: 3/7
     base: 055af92 | updated: 2026-09-20
     next: start Docker, then run PostgreSQL-backed Auth and CTA smoke tests and the full suite -->
# Plan: B0-4 Test Scaffold
## Tasks
- [x] Pin FluentAssertions, MVC testing, Testcontainers PostgreSQL, and Npgsql centrally.
- [x] Add the integration-test project and include it in the solution.
- [x] Make all four service entry points addressable by `WebApplicationFactory`.
- [ ] Add test-only Auth/CTA PostgreSQL provider and `EnsureCreated` initialization for their current SQLite-authored models.
- [ ] Add reusable PostgreSQL-container and service-factory test infrastructure.
- [ ] Add happy-path smoke tests for every concrete service handler and gateway health.
- [ ] Run format verification, the full suite, and an endpoint smoke verification.
## Tests
- Standard rigor: all current handlers, including stateful auth and admin flows; Auth/CTA execute against Testcontainers PostgreSQL. Run `dotnet test KJWebsite.Backend.slnx` and `dotnet format KJWebsite.Backend.slnx --verify-no-changes --no-restore`.
## Files
- `Directory.Packages.props`, `KJWebsite.Backend.slnx`, all service `Program.cs`, and `tests/IntegrationTests/**`.
