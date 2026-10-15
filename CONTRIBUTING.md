# Contributing

Thank you for helping Kolpojontro. This is a volunteer project, so these notes keep pull requests easy to review and merge. By taking part you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## Setup

- Install the **.NET 10 SDK**.
- Restore the local tools (`dotnet-ef`): `dotnet tool restore`.
- Services run with `dotnet run --project <service>`; there is no Docker or CI requirement. See the [README](README.md) for ports, configuration and the database setup.
- PostgreSQL is only needed for Auth and CTA runs and for the optional Postgres tests.

## Branching

- Branch from `develop` using `Feature/KJ-<issue>/<slug>`, for example `Feature/KJ-147/contributing-docs`. This matches the frontend convention.
- `main` is the intended production branch; do not open pull requests against it.
- Open or pick an issue first, and reference it in the pull request.

## Commits and pull requests

- Use [Conventional Commits](https://www.conventionalcommits.org/): `feat(auth): ...`, `fix(cta): ...`, `docs: ...`, `chore: ...`.
- Pull requests are merged with a **merge commit**. Keep history clean before opening one.
- Fill in the pull request template: what, why, how you verified, and whether the API contract or the database changed.

## Local checks

Run these before pushing. Nothing runs them for you.

```bash
dotnet build KJWebsite.Backend.slnx
dotnet format KJWebsite.Backend.slnx --verify-no-changes
dotnet test KJWebsite.Backend.slnx
dotnet run --project tools/OpenApiExporter -- --verify
```

`dotnet test` needs no Docker and no setup: integration tests run against a throwaway SQLite database. To also run the tests that need real PostgreSQL, point `KJ_TEST_POSTGRES` at a local server, for example `Host=localhost;Username=postgres;Password=postgres`. Without it those tests are skipped with a reason. See "Testing" in the README.

For API work, also call the affected route through the gateway.

## API contract rules

- [`openapi.v1.yaml`](openapi.v1.yaml) is the contract the frontend builds against. Keep changes **additive**; never require a frontend change to keep a published page working.
- After changing endpoints or their metadata, regenerate it with `dotnet run --project tools/OpenApiExporter` and commit the result. `--verify` must pass.

## Database and migration rules

- Auth and CTA each own their PostgreSQL schema and migrations. Add migrations with `dotnet ef migrations add <Name>` against the owning service (commands in the README).
- Keep each migration compatible with the previous release (expand, then migrate, then contract). Do not roll migrations back automatically.
- Never edit a migration that has been merged; add a new one.

## Legacy folder

`legacy/` holds read-only snapshots of the 2019 registration app. Do not run, build, restore or copy code from it, and do not change it. See [`legacy/README.md`](legacy/README.md).

## Security

Do not commit secrets, real personal data or production connection strings. Report vulnerabilities privately to contact@kolpojontro.org rather than in a public issue.
