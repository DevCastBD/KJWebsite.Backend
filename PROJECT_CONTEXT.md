# KJWebsite.Backend — Project Context

This is an AI and contributor orientation file: it directs readers to the authoritative documents and repository areas without duplicating implementation, operating, or roadmap details.

## Document ownership

| Need | Authoritative location |
|---|---|
| What the backend will build and why | [Backend master plan](docs/MASTER_PLAN.md) |
| Programme-wide sequencing and README work | [Program master plan](docs/PROGRAM_MASTER_PLAN.md) |
| What exists now; local setup; contribution workflow | [README](README.md) |
| Public API surface | [Committed OpenAPI contract](openapi.v1.yaml) |
| AI-DLC work-item state and repository map | [AI-DLC Nano documents](ai-dlc-nano-documents/) |

The backend master plan is the single source of truth for backend development. If this file, the README, or an issue differs from it, update the plan first and then align the other document.

## Orientation

- Start with the [README](README.md) before running, building, configuring, or contributing to the repository.
- Use the [master plan](docs/MASTER_PLAN.md) to establish scope, phase, engineering standards, and planned architecture before proposing implementation.
- Treat [`openapi.v1.yaml`](openapi.v1.yaml) as the checked-in gateway-level API contract; consult the README for regeneration and verification.
- Service entry points and endpoint mappings are in [`src/Services/`](src/Services/); shared code is in [`src/BuildingBlocks/`](src/BuildingBlocks/).
- [`kj-registration/`](kj-registration/) and [`kj-registration-AwaitingUserFeatures/`](kj-registration-AwaitingUserFeatures/) are historical snapshots. Follow the README's legacy-folder guidance before consulting them.

## Keeping this file useful

- Link to the document that owns a fact instead of copying it here.
- Keep only orientation, document ownership, and repository-navigation context here.
- Update the README for current implementation and developer workflow; update the master plan for scope and roadmap changes.
