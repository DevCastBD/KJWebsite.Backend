<!-- phase: WRAP-UP | branch: Feature/issue-235-publish-permissions | tasks: 4/5
     base: 211118f | updated: 2026-11-18
     next: ask the user before push and PR (never merge); PR text drafted in the session scratchpad
     -->
# Plan: Publish permissions (BE-115, #235)
## Tasks
- [x] 1 Program.cs: user.permissions in TokenResponse and /me from the same data as the token claim
- [x] 2 Tests: every endpoint and role case, equals token claim, edit then login, existing fields unchanged
- [x] 3 openapi.v1.yaml regenerated and verified; docs AUTH_TOKENS.md and RBAC.md
- [x] 4 Full suite, mutation check, format, live check, reviewer
- [ ] 5 Ask before push and PR (never merge)
## Tests
- Standard rigor (additive contract on an identity endpoint): all roles, custom role, two roles, empty lists, token agreement.
## Files
- src/Services/AuthIdentityService/Program.cs (and token response helper); tests; openapi.v1.yaml; docs/AUTH_TOKENS.md, docs/RBAC.md
