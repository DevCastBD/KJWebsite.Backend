# Donation intake (BE-054, #59; closes G17)

`FinanceService` (port 7005, schema `finance`, `ConnectionStrings:FinanceDb`, gateway routes `/api/v1/donations*` and `/api/v1/admin/finance/*`) records donations. Checkout is started at a payment gateway (see PAYMENT_GATEWAYS.md); confirmation arrives with webhooks (#62), Stripe/PayPal (#61) and offline recording (#63), so every donation is `pending` until then. Shapes follow the frontend client (`src/api/endpoints/donations.ts`).

## Money rules
- An amount is `decimal(18,2)` plus an ISO-4217 currency (`BDT`, `USD`, `GBP`, `EUR`), modelled by `Money` in BuildingBlocks. No float or double anywhere; a third decimal is refused, never rounded; adding two currencies throws.
- On the wire an amount is a **decimal string** (`"1250.50"`): plain digits, optional point, at most two decimals. A JSON number, separators, an exponent or non-ASCII digits are `400`.
- Currency limits and presets come from `Finance:Donations` (defaults in `DonationOptions`); `GET /api/v1/donations/presets` publishes them.
- **Cover the fee:** with `coverFees` the charge is `ceil(amount / (1 - FeeRate))` to the cent. `Amount` stays the designated gift, `FeeAmount` the surcharge, `Amount + FeeAmount` what the gateway charges: both are stored so reconciliation balances. `FeeRate` is a placeholder until real gateway fees are known (ADR-008, #111).
- **Multi-currency:** nothing is converted. Totals are one entry per currency. A USD gift to a BDT campaign stays USD; a recorded BDT-equivalent (rate and date) will be added with payment confirmation (#60/#62), not guessed here.

## Append-only
Donations, their events and recurring plans are never updated or deleted. A state change is a new `donation_events` row (latest id wins); a correction is a new `donations` row of kind `reversal` with negated amount and fee, pointing at the reversed donation. A donation can be reversed once and only when `confirmed`; the original then reads `refunded`. Confirmation and reversal also post to the ledger (see LEDGER.md).
Enforced three ways: `FinanceDbContext.SaveChanges` refuses edits, PostgreSQL triggers refuse `UPDATE`/`DELETE` (also on `audit_events`), and a check constraint keeps gifts positive and reversals negative.

## Anonymity
`anonymous: true` donations never expose the donor in any public response (create and status carry no donor detail at all). Finance staff (`finance.view`) can read donor name, email and phone in the back office, because receipts and audits need them, with the `anonymous` flag on every row. Donor fields are encrypted at rest (Data Protection, keys at `DataProtection:KeysPath`) and kept out of the audit log. Any future export that leaves the finance team must go through a projection that omits the donor when `anonymous`.

## Public API
| Endpoint | Notes |
|---|---|
| `GET /api/v1/donations/presets` | currencies, presets, min/max, frequencies |
| `POST /api/v1/donations` | `Idempotency-Key`; 201 `{id, reference, status, amount, currency, fee?, total?, gateway?, checkoutUrl?, fallback?}`; rate limit 20/h/IP |
| `GET /api/v1/donations/{reference}/status` | `{reference, status, amount, currency, anonymous, gateway?}`; 404 for unknown; never cached |
| `GET\|POST /api/v1/donations/{reference}/return` | gateway return: `303` to the site's status page, changes nothing |

Request: `{amount, currency, frequency: once|monthly|yearly, designation: {type: general|campaign|project, id?}, donor: {name, email, phone?}, anonymous, dedication?, coverFees, language: en|bn, gateway?: bkash|nagad|sslcommerz}`. Campaign designations must name an open campaign (see CAMPAIGNS.md); project ids are opaque slugs. `monthly`/`yearly` also creates a recurring plan; later instalments are charged by the gateway work.
`reference` is `KJD-` plus 10 random Crockford base32 characters, so a status page cannot be guessed.

## Back office (`/api/v1/admin/finance`)
| Endpoint | Permission |
|---|---|
| `GET donations?from&to&status&designation&limit&offset` | `finance.view` |
| `GET donations/{id}`, `GET donations/totals?designation`, `GET recurring-plans`, `GET audit` | `finance.view` / `audit.view` |
| `POST donations/{id}/reversal {reason}` | `finance.donation.reverse` (Admin and SuperAdmin only until the four-eyes controls, #72) |

Status is `pending`, `confirmed`, `failed`, `cancelled` or `refunded`. `method` is the gateway of the latest started checkout, `null` until one exists (offline recording, #63, adds the rest); `?method=` filters on it.
