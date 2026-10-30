<!-- phase: DONE | branch: feature/issue-29-validation | tasks: 4/4
     base: c8afdf1 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: FluentValidation with RFC 9457 Problem Details
## Tasks
- [x] BuildingBlocks: ValidationFilter<T>, localized message provider (bn/en), Problem Details errors map
- [x] Validators for Auth, CTA, Content request contracts; replace inline checks
- [x] Tests: per-field errors, bn/en, legacy shape kept, every POST/PUT/PATCH covered
- [x] Docs, openapi (400 shape), full suite + format
## Tests
- Standard: unit (validators) + integration
## Files
- src/BuildingBlocks, src/Services/*/Program.cs + Validators, tests, docs
