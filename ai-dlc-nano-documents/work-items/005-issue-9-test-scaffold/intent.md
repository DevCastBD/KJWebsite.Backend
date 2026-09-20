# B0-4 Test Scaffold
- Source: issue #9 https://github.com/DevCastBD/KJWebsite.Backend/issues/9
- Type: feature
## Request
- Scaffold unit and integration test projects using xUnit, FluentAssertions, WebApplicationFactory, and Testcontainers.
- Run integration tests against a real PostgreSQL container.
- Add smoke coverage for every endpoint that currently exists.
- Make `dotnet test` runnable locally and ready for CI.
## Decisions
- Auth and CTA integration hosts use Testcontainers PostgreSQL through test-only configuration; default runtime remains SQLite.
- Smoke tests cover all concrete handlers and gateway health; multi-service gateway proxy orchestration is excluded.
## Out of scope
- Reworking production services from SQLite to PostgreSQL beyond what the test harness requires.
## Follow-ups
- None.
