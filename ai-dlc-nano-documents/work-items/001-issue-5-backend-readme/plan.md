<!-- phase: DONE | branch: 5-feature-b0-1-write-the-backend-readme-including-the-not-production-safe-security-notice | tasks: 6/6
     base: 7aac7bb | updated: 2026-09-20
     next: report completed work; no commit or remote action requested -->
# Plan: Backend README and OpenAPI exporter
## Tasks
- [x] Add the cross-platform `tools/OpenApiExporter` project to the solution.
- [x] Implement development-service orchestration, runtime OpenAPI retrieval, deterministic merge, YAML output, and `--verify` drift detection.
- [x] Regenerate `openapi.v1.yaml` with the exporter and verify its aggregate contract.
- [x] Replace `README.md` with the agreed shared-template documentation, Mermaid topology, accurate local run/configuration/API/legacy guidance, and an explicit security warning.
- [x] Document exporter usage, its runtime prerequisites, and frontend contract consumption in the README.
- [x] Build the full solution and run the exporter smoke/verify flow; re-check all README commands and claims against the repository.
## Tests
- Standard rigor: `dotnet build KJWebsite.Backend.slnx`; exporter generation and `--verify`; inspect the generated YAML for all gateway routes. No existing test project exists, so no full unit-test suite is available.
## Files
- `KJWebsite.Backend.slnx`, `tools/OpenApiExporter/**`, `openapi.v1.yaml`, `README.md`
