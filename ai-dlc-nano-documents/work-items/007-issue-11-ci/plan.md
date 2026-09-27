<!-- phase: DONE | branch: 11-feature-b0-6-github-actions-ci | tasks: 7/7
     base: 7c8ce2f | updated: 2026-09-27
     next: — (PR #130 ready; CI green; develop protected) -->
# Plan: B0-6 — GitHub Actions CI
## Tasks
- [x] .github/workflows/ci.yml, job `build`: setup-dotnet 10, NuGet cache, restore → build (Release) → format --verify-no-changes → test (unit + Testcontainers integration; Docker is on ubuntu runners) → trx results as artefact
- [x] Same job: OpenAPI exporter --verify (fails on drift) + upload openapi.v1.yaml artefact
- [x] Job `dependency-scan`: `dotnet list package --vulnerable --include-transitive --format json`, fail on Critical, table of all findings in the job summary
- [x] .github/dependabot.yml: nuget + github-actions, weekly
- [x] Triggers on PR + push to develop/main; concurrency cancels superseded runs; `permissions: contents: read`; timeout 10 min
- [x] README CI badge
- [x] Run it for real on the PR; fix what the first-ever integration run exposes (005's merged-but-unrun tests); then set `build` + `dependency-scan` as required checks on develop via the API
## Optional (your call at approval)
- [ ] Job `compose-smoke`: `docker compose up`, probe each /health — the first execution of 006's Compose stack. Non-required, so a slow image build cannot block merges
## Tests
- Local on .NET 10: restore/build/format/unit/OpenAPI already green; workflow YAML validated before push
- Real proof is the Actions run on the PR (Docker-backed tests can only run there)
## Files
- new: .github/workflows/ci.yml, .github/dependabot.yml, .github/scripts/check-vulnerable.py
- edited: README.md; possibly tests/IntegrationTests/** if the first run exposes failures
