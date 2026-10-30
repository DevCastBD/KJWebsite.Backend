# FluentValidation with RFC 9457 Problem Details
- Source: issue #29 https://github.com/DevCastBD/KJWebsite.Backend/issues/29
- Docs: docs/MASTER_PLAN.md, ADR-009, docs/CONTENT_CONTRACT.md
- Type: feature
## Request
- Shared FluentValidation endpoint filter in BuildingBlocks; Problem Details with stable type URI and per-field errors; bn+en messages; migrate inline checks (Auth, CTA, Content).
- Keep legacy { error: { code, message } } alongside (deprecation note), do not remove.
## Decisions
- Language from existing LanguageResolver; messages in resx-style dictionary for bn/en → assumed
- Problem Details emitted with legacy error object as extension member → matches current factory
## Out of scope
- Removing legacy error shape
## Follow-ups
- none yet
