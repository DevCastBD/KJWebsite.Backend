# ADR-013: Manual `dotnet publish` deploys for now; provider and budget deferred

- Status: Accepted (partial)
- Date: 2026-10-14
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12), [issue #106](https://github.com/DevCastBD/KJWebsite.Backend/issues/106)

## Context

The original plan recommended container-based hosting (Azure Container Apps, AWS App Runner, Hetzner + Docker Compose, or Fly.io), chosen on cost. Plan v2 (2026-10-12) ruled out Docker and CI/CD at this stage, and the monthly budget ceiling and provider choice is a core-member decision still open on #106. This ADR settles only what is needed to unblock P1 work now; it does not settle the open budget/provider question.

## Decision

Deploy manually with `dotnet publish` for each service, no containers, until #106 settles the budget and provider. The specific hosting provider and container-vs-not decision for production is deferred to #106 and will be recorded as a follow-up ADR once that decision lands.

## Rejected alternatives

- Containerizing now, ahead of a settled budget and provider: adds Docker/CI operational complexity before either exists at this stage (plan v2), and risks being redone once #106 picks a provider whose constraints may favor a different container story.

## Consequences

- Deploys are manual (`dotnet publish` + copy/run) until #106 is resolved and a follow-up ADR records the production hosting decision.
- No CI/CD pipeline is required by this decision; one may still be added separately (tracked in `ai-dlc-nano-documents/backlog.md`, which already flags that `.github/workflows/ci.yml` exists but is not fully wired for the current test setup).
- Services remain independently publishable per ADR-001, so the eventual hosting choice does not require re-architecting service boundaries.
