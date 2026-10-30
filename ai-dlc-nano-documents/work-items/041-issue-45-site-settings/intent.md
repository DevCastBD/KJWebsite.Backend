# BE-037 Site settings (#45)
- Source: issue #45 (milestone 3, order BE-037)
- Type: feature
## Request
- Bilingual editable settings: navigation, contact, social, footer, homepage section order, maintenance mode; runtime feature flags; secret settings never public
## Decisions
- Stacked on #40 branch; #41 skipped by user
- Catalog of typed keys (validation requires bn+en on every text) plus custom.* bilingual text settings (may be secret)
- Runtime flags named "runtime flags" (/flags), distinct from frontend VITE_ENABLE_* build flags
- Public settings endpoint never errors for maintenance (200 with maintenance block); content endpoints are not blocked
## Out of scope
- Encrypting secret values (plain DB value, never public; #36 secrets later)
## Follow-ups
