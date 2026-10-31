# Receipts and annual statements (BE-059, #64)

## Numbering
Every confirmed donation gets exactly one receipt, numbered **`KJR-{fiscal year}-{sequence}`** (for example `KJR-2026-27-000001`). The series restarts each fiscal year (the same `Finance:Ledger:FiscalYearStartMonth` as the ledger, July by default; the year comes from the date the gift was received) and runs 1, 2, 3 with no gaps.

- **Issued with the confirmation.** The receipt is saved in the same commit as the `confirmed` event and the ledger posting, for every path (gateway webhook, resolver, offline approval). A confirmed donation always has one; an unconfirmed one never does.
- **Gapless under concurrency.** A number is the series' highest plus one, and `(series, sequence)` is unique. Two confirmations racing for the same number: the database lets one win, the other's whole commit fails and is retried with the next number. Nothing is ever deleted (application guard and PostgreSQL triggers), so a number cannot be skipped. A test confirms 20 donations at once on PostgreSQL and checks 1..20.
- **Tamper-evident.** Each receipt stores the hash of the previous one in its series and its own hash (SHA-256 over number, sequence, previous hash, donation, amount, currency, date received, donor key and issue time). `GET /api/v1/admin/finance/receipts/verify?series=2026-27` walks the series and reports any gap, broken link or altered receipt.

## What is on a receipt
A snapshot taken at issue: donor, amount and currency, the fee covered (shown as not part of the gift), method, designation, date received and issued, and for an in-kind gift the item and valuation basis. The organisation block (names in both scripts, registration number, address, footer) comes from `Finance:Receipts:Organisation` and is printed as set; **what a receipt must legally carry depends on the organisation's legal status (#114, open), so nothing is invented and the registration number is blank until set.** The PDF is bilingual (English and Bengali labels), uses an embedded Noto Sans Bengali so conjuncts render on a server without fonts, and prints the receipt number and amount in Bengali numerals as well as Latin digits.

## Access
- Donor: `GET /api/v1/donations/{reference}/receipt.pdf` (the unguessable reference is the key, as for the status page; never cached). The donation status gains `receiptUrl` once a valid receipt exists. A reversed donation's receipt is gone (404).
- Staff (`finance.view`): `GET /api/v1/admin/finance/receipts` (filters `series`, `status=valid|void`), `GET .../receipts/{id}`, `GET .../receipts/{id}/pdf`.

## Void and re-issue (both additive)
- **Void:** reversing the donation appends a void row; the receipt keeps its number (so the series stays gapless) and its PDF reads VOID.
- **Re-issue:** `POST .../receipts/{id}/reissue {reason, resend?}` (`finance.receipt.manage`) records who asked and why and leaves the original untouched; later PDFs are marked DUPLICATE COPY n; `resend` queues the email again. A void receipt cannot be re-issued.

## Email
A message is written to the Finance `notification_outbox` in the same commit (template `donation.receipt`, recipient and payload data-protected) when the donor gave an email address; there is no sender yet (BE-032/033), so rows wait with `SentAt` empty, as in the membership outbox. A mail failure can never block a donation. A donor with no email (a walk-in at the office) still gets a receipt to print.

## Annual statements
Donors are grouped by a keyed hash of their email (`Finance:Receipts:DonorKeySecret`, required outside Development; changing it splits donors); a donor with no email stands alone per gift. `GET .../statements?fiscalYear=2026-27` lists each donor's receipt count and per-currency totals (money and in-kind value apart; voided receipts excluded), `GET .../statements/{donorKey}/pdf?fiscalYear=` renders it, and `POST .../statements/send {fiscalYear}` (`finance.receipt.manage`) queues one email per donor with an address, once per donor and year. Anonymous gifts appear only in their own donor's statement.

## Donors
The donor key also drives the donor CRM (DONORS.md): a receipt is issued first, then the donor record and any consented thank-you are saved in the same commit.
