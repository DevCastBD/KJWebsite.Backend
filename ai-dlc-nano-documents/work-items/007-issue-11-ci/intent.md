# B0-6 — GitHub Actions CI (restore, build, format, test, OpenAPI artefact)
- Source: issue #11 https://github.com/DevCastBD/KJWebsite.Backend/issues/11 (P0, gap G9)
- Type: feature
## Request
- CI on every PR: restore → build → dotnet format --verify-no-changes → test → publish openapi.v1.yaml artefact
- Dependency scanning (dotnet list package --vulnerable + Dependabot), 7-day critical-patch SLA
- CI badge in README; under 10 minutes; a PR failing build/format/tests cannot merge
## Context found at intake
- 005 (test scaffold) and 006 (docker-compose) were merged (#128, #129) but never executed: both stalled on "needs Docker"
- This machine has .NET 7.0.307 only (repo targets net10.0) and no Docker — nothing can be built or tested locally
- So CI is the first environment where the merged tests and Compose stack actually run; expect real failures
## Decisions
- Local SDK → .NET 10.0.401 installed user-locally in ~/.dotnet10, system .NET 7 untouched (user chose)
- Branch protection → I set required checks on develop via the API once CI is green, showing the rule first (user chose)
- OpenAPI → CI runs the exporter's --verify (fail on drift) and uploads the file as an artefact; no bot commits (user chose)
- Scanning → fail on critical, report the rest; Dependabot weekly (user chose). `dotnet list --vulnerable` always exits 0, so JSON output is parsed
- Local baseline (net10): restore, build (0 warnings), format, 6 unit tests and OpenAPI --verify all pass; integration tests need Docker
## Out of scope
- P1 security gaps G1–G3 (tracked separately)
## Follow-ups
