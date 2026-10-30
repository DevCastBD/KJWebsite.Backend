# KJWebsite.Backend — Project Context

This is an AI and contributor orientation file: it directs readers to the authoritative documents and repository areas without duplicating implementation, operating, or roadmap details.

## Document ownership

| Need | Authoritative location |
|---|---|
| What the backend will build, why, and in which order | [Backend master plan](docs/MASTER_PLAN.md) |
| What exists now; local setup; contribution workflow | [README](README.md) |
| Public API surface | [Committed OpenAPI contract](openapi.v1.yaml) |
| AI-DLC work-item state and repository map | [AI-DLC Nano documents](ai-dlc-nano-documents/) |

The backend master plan is the single source of truth for backend development. If this file, the README, or an issue differs from it, update the plan first and then align the other document.

## Orientation

- Start with the [README](README.md) before running, building, configuring, or contributing to the repository.
- Use the [master plan](docs/MASTER_PLAN.md) to establish scope, phase, engineering standards, and planned architecture before proposing implementation.
- Treat [`openapi.v1.yaml`](openapi.v1.yaml) as the checked-in gateway-level API contract; consult the README for regeneration and verification.
- Service entry points and endpoint mappings are in [`src/Services/`](src/Services/); shared code is in [`src/BuildingBlocks/`](src/BuildingBlocks/).
- The frontend checkout lives next to this repository at `/Users/fhshubho/Workspace/Repo/kolpojontro/KJWebsite.FrontEnd` (the `KJWebsite.FrontEnd` repository). Build and change the backend **aligned with it**: before implementing or editing an endpoint, contract, setting or language behaviour, read how the frontend consumes it there (its API client, `config.json` capability flags, static fallbacks) and keep responses additive so the static build keeps working. If the frontend and this repo disagree, raise it rather than guessing. That path is specific to this developer's machine; elsewhere use your own clone of the frontend repository. AI agents get the same instruction from [`AGENTS.md`](AGENTS.md) (`CLAUDE.md` imports it).
- [`legacy/kj-registration/`](legacy/kj-registration/) and [`legacy/kj-registration-AwaitingUserFeatures/`](legacy/kj-registration-AwaitingUserFeatures/) are historical snapshots. Read [`legacy/README.md`](legacy/README.md) before consulting them.

## Keeping this file useful

- Link to the document that owns a fact instead of copying it here.
- Keep only orientation, document ownership, and repository-navigation context here.
- Update the README for current implementation and developer workflow; update the master plan for scope and roadmap changes.
