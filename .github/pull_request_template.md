## What

<!-- What does this change? Link the issue: Closes #NNN -->

## Why

<!-- The problem or goal. -->

## How I verified it

<!-- Commands run and results; routes exercised through the gateway. -->

- [ ] `dotnet build KJWebsite.Backend.slnx`
- [ ] `dotnet format KJWebsite.Backend.slnx --verify-no-changes`
- [ ] `dotnet test KJWebsite.Backend.slnx`

## API contract

- [ ] No API change, **or** `openapi.v1.yaml` is regenerated and `dotnet run --project tools/OpenApiExporter -- --verify` passes
- [ ] The change is additive; no frontend change is required to keep a published page working

## Database

- [ ] No database change, **or** a migration is included, compatible with the previous release, and the owning service is named above

## Checklist

- [ ] Branch is `Feature/KJ-<issue>/<slug>` off `develop`; commits follow Conventional Commits
- [ ] Tests added or updated for behavior that can regress
- [ ] README and docs updated where behavior changed
- [ ] No secrets or real personal data committed
