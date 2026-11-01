<!-- phase: DONE | branch: Feature/issue-72-controls (proposed, stacked on Feature/issue-71-reports) | tasks: 8/8
     base: 200c277 | updated: 2026-11-01
     next: await plan + branch approval -->
# Plan: Financial controls (BE-068, #72)
## Tasks
- [x] Entities + migration: control_requests, control_decisions (unique per request, PG trigger: approver != requester), close_events, close-table delete guard trigger; permission finance.control.approve; role seeds
- [x] ControlService: propose/approve/reject/withdraw with per-kind validation and atomic execution (adjustment, posting_reversal, refund, period_reopen, fiscal_year_reopen)
- [x] Extract donation reversal into a service used by the refund executor; LedgerService reopen + close events; guard allows close-row delete only with a reopen event
- [x] Endpoints: requests list/get/approve/reject/withdraw; existing adjustment, posting reversal, donation reversal and new reopen endpoints create requests; refunds list with gateway reconciliation and unexpected notices
- [x] Audit registration and audit-coverage test; docs/CONTROLS.md with go-live review checklist; update dependent docs; openapi
- [x] Update existing tests that posted adjustments or reversed donations directly (helper that proposes and approves with two actors)
- [x] New tests: submit vs approve (self-approval refused at service and PG), distinct approver executes once under concurrency, reject/withdraw post nothing, validation at propose and at approve (closed period), reopen then re-close, delete guard on PG, refund reconcile (match, mismatch, unexpected), permissions, audit coverage
- [x] Full suite twice with PG, format, OpenAPI verify
## Tests
- Controls are the product: every bypass route (old endpoints, direct DB delete, self-approval, double approval) gets a test
## Files
- src/Services/FinanceService/Controls/**, Ledger/**, Donations/AdminDonationEndpoints.cs, Data/**, Migrations/**, Program.cs; BuildingBlocks Permissions; tests/**; docs/**; openapi.v1.yaml
