# Stripe and PayPal adapters (BE-057)
- Source: issue #61 https://github.com/DevCastBD/KJWebsite.Backend/issues/61 (milestone 5, P4)
- Docs: docs/PAYMENT_GATEWAYS.md, ADR-008
- Type: feature (high blast radius: money, public webhooks)
## Request
- Stripe and PayPal behind the same IPaymentGateway; sandbox donation completes through both; settlement vs designation amounts recorded distinctly; refunds/disputes surfaced through the same workflow as local gateways.
## Decisions
- Settlement: separate append-only payment_settlements row on confirmation (settled amount/currency, gateway fee, reported exchange rate, transaction id); designated amount untouched; progress stays per designated currency, never converted (user)
- Refund/dispute events: verified (signed) and stored as append-only provider_notices + admin list + warning log; no automatic reversal; reversal stays the human endpoint until #72 (user)
- Flow: Stripe Checkout Session + Stripe-Signature HMAC, session re-fetch decides; PayPal Orders v2, capture on approval then re-fetch, webhooks verified via PayPal verify-webhook-signature (user)
- Assumed: interface parse becomes async (PayPal verification is a network call; a failed check -> 503 retry); IncomingWebhook carries headers + raw body; BDT not offered by PayPal; per-currency default order puts stripe/paypal first for non-BDT; refund/dispute lookup via donation attempt id or settlement transaction id; adapters tested against fakes only
## Out of scope
- Refund/dispute workflow and chargeback ledger entries (#72), offline recording (#63)
## Follow-ups
- Run Stripe and PayPal against their sandboxes once accounts exist (#111); PayPal webhook and Stripe payload shapes follow published docs
- Refund/dispute workflow, chargeback ledger entries, gateway refund reconciliation: #72 (notices are the input)
- Progress totals across currencies: #66 (settlement is recorded, designated currency drives progress)
