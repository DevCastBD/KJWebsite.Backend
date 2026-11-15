# Offline donation recording with proof and maker-checker (BE-058)
- Source: issue #63 https://github.com/DevCastBD/KJWebsite.Backend/issues/63 (milestone 5, P4)
- Docs: docs/DONATIONS.md, docs/LEDGER.md; FE contract POST /donations (src/admin/finance/api.ts recordDonation)
- Type: feature (high blast radius: manual money entry)
## Request
- Record bank/cash/cheque/mobile-wallet/in-kind gifts; a different user must approve before it posts; proof attaches and is retained; in-kind captures valuation + basis; every step audited.
## Decisions
- Proof: Finance-owned IProofStore (local disk now), PDF/JPEG/PNG/WebP <= 5 MB content-sniffed, SHA-256 recorded, append-only, no delete API, retained per config (default 7 years; #110 undecided) (user)
- In-kind: Dr new asset 1250 "In-kind gifts received" / Cr donation income; valuation amount + basis + item description required (user)
- Assumed: FE path POST /api/v1/admin/finance/donations creates a pending donation + offline record (method incl. in_kind, designation as "general"|"campaign:slug"|"project:slug", date = received date); approve/reject at donations/{id}/approve|reject; approver != recorder enforced server-side (403 SEPARATION_OF_DUTIES); proof required to approve for bank/cheque/bkash/nagad, optional for cash/in-kind; posting dated received date unless that month is closed, then today; reject cancels the donation; new perms finance.donation.record and finance.donation.approve (Admin + FinanceOfficer); admin list shows method/offline from the record; email optional for offline donors
## Out of scope
- Thresholds and four-eyes beyond different-person approval (#72), receipts (#64), donor CRM (#65)
## Follow-ups
- Proof retention period (default 7y) waits on #110; proof bytes move to object storage (ADR-006)
- Approval thresholds / further four-eyes: #72; receipts for approved gifts: #64
- Offline mobile-wallet gifts could be matched to wallet statements in #70 via referenceNo
