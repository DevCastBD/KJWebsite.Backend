# Offline donations with maker-checker (BE-058, #63)

Most gifts arrive outside the gateways: bank transfers, cash, cheques, bKash/Nagad sent to a number, goods. They are recorded in the back office under **maker-checker**: one person records, a **different** person approves, and only the approval posts to the ledger. The rule is enforced on the server from the signed-in user ids (`403 SEPARATION_OF_DUTIES`), whatever permissions the user holds.

## Flow
1. **Record** (`finance.donation.record`): `POST /api/v1/admin/finance/donations` (the frontend's path) with `{donorName, email?, phone?, amount, currency, method, date?, designation?, anonymous?, dedication?, referenceNo?, note?, valuation?}`. `method` is `bank`, `cash`, `cheque`, `bkash`, `nagad` or `in_kind`; `date` is when it arrived (not in the future, not older than two years); `designation` is `general`, `campaign:slug` or `project:slug`; `referenceNo` is the bank/cheque/wallet transaction id to match statements. The donation is created **`pending`** with an offline record and posts nothing. Accepts an `Idempotency-Key`. The email is optional (a walk-in donor may not give one).
2. **Prove** (`finance.donation.record`): `PUT .../donations/{id}/proofs` with the raw file, its `Content-Type` and an optional URL-encoded `X-Filename`. PDF, JPEG, PNG or WebP up to 5 MB, content-checked against the file's own bytes, up to ten per donation, only while awaiting a decision. The SHA-256 and a retention date are stored. `GET .../proofs/{proofId}/file` (`finance.view`) downloads it as an attachment, never cached.
3. **Decide** (`finance.donation.approve`): `POST .../approve` or `POST .../reject {reason}`. Approving confirms the donation and posts it to the ledger in **one commit** with the decision record, dated the day the gift arrived (or today when that month is already closed). A bank, cheque or wallet gift needs at least one proof first (`409 PROOF_REQUIRED`); cash and in-kind do not. Approving twice is harmless; one decision per record (unique in the database). Rejecting cancels the donation with the reason; a mistake is rejected and recorded again, never edited.
4. `GET .../donations/{id}/offline` and the donation detail show the record, proofs, decision and valuation; the donations list shows `method` and `offline: true`.

## In-kind gifts
The donation amount is the **valuation**, and the record must carry `valuation: {basis, itemDescription, note?}` with `basis` one of `market_price`, `appraisal`, `donor_receipt`, `cost`, `other` (a note is required for `other`). They post Dr `1250 In-kind gifts received` (asset) / Cr donation income. Handing the item on later is a manual ledger adjustment.

## Retention and evidence
Offline records, decisions and proofs are append-only (application guard and PostgreSQL triggers); no endpoint deletes or edits them. Proof files carry `retainUntil` = upload + `Finance:Proofs:RetentionYears` (default 7, cautious until the retention decision #110). Proof bytes live behind `IProofStore` (local disk at `Finance:Proofs:Path` for now; object storage per ADR-006 later). Every step is audited with its own actor: the donation and record (maker), each proof (uploader), the decision (checker).

## Permissions
`finance.donation.record` and `finance.donation.approve` are held by Admin, SuperAdmin and FinanceOfficer. Further four-eyes controls (adjustments, refunds, reopen) are in CONTROLS.md.
