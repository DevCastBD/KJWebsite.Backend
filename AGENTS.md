# Instructions for AI coding agents

Read [`PROJECT_CONTEXT.md`](PROJECT_CONTEXT.md) first, then the [README](README.md) and the [master plan](docs/MASTER_PLAN.md) (the plan wins when documents disagree).

## Align with the frontend

The frontend repository (`KJWebsite.FrontEnd`) is checked out next to this one at `/Users/fhshubho/Workspace/Repo/kolpojontro/KJWebsite.FrontEnd`. Build and change the backend **aligned with it**:

- Before implementing or editing an endpoint, contract, setting, error shape or language behaviour, read how the frontend consumes it there (API client, `config.json` capability flags, static fallbacks, routing).
- Keep API changes **additive**: the public site is a static build and must keep rendering with this API switched off or older.
- If the frontend and this repository disagree, say so and ask instead of guessing.
- That path is specific to one machine; on another, use your own clone of the frontend repository.

## Working rules

- Keep `openapi.v1.yaml` current (`dotnet run --project tools/OpenApiExporter`, verify with `-- --verify`), and run build, `dotnet format ... --verify-no-changes` and `dotnet test KJWebsite.Backend.slnx` before finishing. No CI or Docker at this stage.
- Add no AI attribution anywhere (commits, PRs, code, docs).
