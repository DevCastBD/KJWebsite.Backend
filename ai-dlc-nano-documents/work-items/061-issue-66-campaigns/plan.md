<!-- phase: DONE | branch: Feature/issue-66-campaigns | tasks: 9/9
     base: 69fe98a | updated: 2026-10-31
     next: none (wrapped up) -->
# Plan: Campaigns and public progress (BE-061, #66)
## Tasks
- [x] Entity + migration: campaigns (unique slug), options
- [x] Admin API (FE shape): list/create/update campaigns with raised, status transitions, audit; permission finance.campaign.manage
- [x] Progress service: derived from valid receipts per goal currency + otherCurrencies, TTL cache with single-flight and stale-on-error, never counts pending/void
- [x] Public API: GET /api/v1/campaigns/{slug}/progress (FE shape) with Cache-Control/ETag; gateway route + rate limit
- [x] Designation validation: campaign must exist and accept gifts (public + offline); fix existing tests
- [x] Reconcile endpoint: cached vs recomputed vs ledger income per campaign
- [x] Docs (docs/CAMPAIGNS.md), openapi regen, map
- [x] Tests: CRUD/validation, progress correctness (pending/void/foreign currency/anonymous counted?), cache hit/refresh/stale, ETag 304, spike (many concurrent requests -> one computation), designation rules, reconcile, permissions, PG
- [x] Full suite, format, OpenAPI verify, coverage check
## Tests
- Cache behaviour (single compute under concurrency), numbers that match the ledger exactly
## Files
- src/Services/FinanceService/Campaigns/**, Data/**, Migrations/**, Donations/*, Offline/*, ApiGateway, Program.cs; BuildingBlocks Permissions; tests/**; docs/**; openapi.v1.yaml
