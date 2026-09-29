<!-- phase: WRAP-UP | branch: 13-feature-b0-9-move-kj-registration-snapshots-to-legacy | tasks: 4/4
     base: 93603f4 | updated: 2026-09-30
     next: push branch, open PR (closes #13), comment on #13; CI must pass -->
# Plan: move kj-registration snapshots to legacy/ (fast path)
## Tasks
- [x] git mv both snapshot folders into legacy/; drop tracked .vs/, bin/, obj/ junk; git-ignore it
- [x] legacy/README.md: read-only warning, what to mine, what never to run, dump check result
- [x] Update path references: .dockerignore, README, PROJECT_CONTEXT, codebase-map, two source comments
- [x] Verify: Release build, legacy still excluded, format, unit + non-Docker tests, renames in git (Docker tests left to CI)
## Tests
- none new — file move + docs; verified by build, existing suites, and git rename detection
## Files
- legacy/** (moved), legacy/README.md, .gitignore, .dockerignore, README.md, PROJECT_CONTEXT.md
- ai-dlc-nano-documents/codebase-map.md, AuthIdentityService/Contracts/UserContracts.cs, CtaSubmissionService/Contracts/VolunteerContracts.cs
