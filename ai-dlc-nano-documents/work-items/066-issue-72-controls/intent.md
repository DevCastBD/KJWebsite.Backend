# Financial controls: four-eyes, refunds, reopen (BE-068)
- Source: issue #72 https://github.com/DevCastBD/KJWebsite.Backend/issues/72 (milestone 5, P4; last of the milestone's chosen issues)
- Docs promising this issue: LEDGER.md, BUDGETS.md, EXPENSES.md, DONATIONS.md, PAYMENT_GATEWAYS.md (refund workflow, reconcile gateway refunds), OFFLINE_DONATIONS.md
- Type: feature (highest blast radius: changes how adjustments and refunds reach the ledger)
## Request
- Segregation of duties (submit vs approve), approval thresholds, audit of every financial mutation, four-eyes on ledger adjustments, refund workflow reconciled with the gateway; enforced server-side; external review before go-live.
## Decisions
- Adjustments need one distinct approver besides the proposer; applies to manual adjustments and posting reversals (user)
- Refunds: ledger workflow plus reconciliation (request, distinct approver, reversing entry; signed provider notice matched to the request; mismatches and unexpected notices listed); no live gateway refund calls (user)
- Period and fiscal-year reopen included: reason, proposer, distinct approver (user)
- Assumed: one generic control-request mechanism (control_requests + control_decisions, kinds adjustment, posting_reversal, refund, period_reopen, fiscal_year_reopen); the approval executes the action in the same commit as the decision (staged posting, so no half-done state), one decision per request enforced by a unique index; approving your own request is refused by the service and by a PostgreSQL trigger; proposer may withdraw; rejected/withdrawn requests post nothing; the existing direct endpoints (POST ledger/postings, posting reversal, donation reversal) now create requests (202) and no longer post directly, since a direct path would defeat the control; approval needs new permission finance.control.approve; FinanceOfficer gains ledger.adjust, donation.reverse, period.close and control.approve so two different people can act (Admin and SuperAdmin keep all); close tables stay append-only except that a close row may be deleted only when an approved, distinct-approver reopen event exists (PostgreSQL trigger) and every close and reopen is written to close_events; thresholds: expense tiers already exist (BE-067), adjustments and refunds are four-eyes at any amount; audit coverage test enumerates every financial entity and requires it to be audit-registered
## Out of scope
- Live gateway refund API calls, dispute workflow beyond listing notices, the external review itself (a review checklist is delivered; the review is a go-live gate that a person must complete)
## Follow-ups
