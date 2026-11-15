<!-- phase: DONE | branch: Feature/issue-59-donation-intake | tasks: 8/8
     base: efa2bc6 | updated: 2026-10-31
     next: none (wrapped up) -->
# Plan: Donation intake domain (BE-054, #59)
## Tasks
- [x] Money value object (decimal 18,2, ISO-4217 allow-list BDT/USD/GBP/EUR, exact add/sub/negate, fee-cover) in BuildingBlocks
- [x] FinanceService scaffold: csproj, Program (port 7005, schema finance, FinanceDb), slnx, gateway route + rate limit, OpenAPI exporter entry
- [x] Entities: Donation (immutable), DonationEvent (Created/Reversed...), RecurringPlan(+events); Npgsql migration; PII (donor name/email) via field protector
- [x] Append-only guard: SaveChanges interceptor rejects update/delete of donation rows; PG trigger in migration
- [x] Public API: GET presets, POST donations (idempotent, Pending, amount+fee=charged, anonymous, dedication, designation, recurring plan)
- [x] Admin API (finance.view): list/detail with donor; reversal entry (new perm finance.donation.reverse, Admin only); per-currency totals
- [x] Permissions catalog + seed, audit config, docs/DONATIONS.md, regenerate openapi.v1.yaml, codebase-map
- [x] Tests: money property tests, append-only, anonymity, validation, idempotency, PG-gated test
## Tests
- High blast radius (money): unit + property tests (seeded random), integration on SQLite, PG-gated migration/trigger test; full suite + format + OpenAPI --verify
## Files
- src/BuildingBlocks/.../Money*.cs, Security/Permissions.cs; src/Services/FinanceService/**; gateway appsettings; tools/OpenApiExporter; slnx; tests/**; docs/DONATIONS.md; openapi.v1.yaml
