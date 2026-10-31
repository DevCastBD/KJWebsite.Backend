<!-- phase: PLAN | branch: feature/issue-29-validation | tasks: 0/4
     base: — | updated: 2026-10-30
     next: await plan approval, then branch feature/issue-29-validation (feature/issue-28-rate-limiting) -->
# Plan: FluentValidation with RFC 9457 Problem Details
## Tasks
- [ ] BuildingBlocks: ValidationFilter<T>, localized message provider (bn/en), Problem Details errors map
- [ ] Validators for Auth, CTA, Content request contracts; replace inline checks
- [ ] Tests: per-field errors, bn/en, legacy shape kept, every POST/PUT/PATCH covered
- [ ] Docs, openapi (400 shape), full suite + format
## Tests
- Standard: unit (validators) + integration
## Files
- src/BuildingBlocks, src/Services/*/Program.cs + Validators, tests, docs
