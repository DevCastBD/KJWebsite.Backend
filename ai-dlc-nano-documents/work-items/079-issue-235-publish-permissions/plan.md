<!-- phase: CONSTRUCT | branch: Feature/issue-235-publish-permissions | tasks: 0/5
     base: a00e162 | updated: 2026-11-18
     next: Codex: permissions in TokenResponse and /me, tests, openapi, docs -->
# Plan: Publish permissions (BE-115, #235)
## Tasks
- [ ] 1 Program.cs: user.permissions in TokenResponse and /me from the same data as the token claim
- [ ] 2 Tests: every endpoint and role case, equals token claim, edit then login, existing fields unchanged
- [ ] 3 openapi.v1.yaml regenerated and verified; docs AUTH_TOKENS.md and RBAC.md
- [ ] 4 Full suite, mutation check, format, live check, reviewer
- [ ] 5 Ask before push and PR (never merge)
## Tests
- Standard rigor (additive contract on an identity endpoint): all roles, custom role, two roles, empty lists, token agreement.
## Files
- src/Services/AuthIdentityService/Program.cs (and token response helper); tests; openapi.v1.yaml; docs/AUTH_TOKENS.md, docs/RBAC.md
