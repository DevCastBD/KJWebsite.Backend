<!-- phase: DONE | branch: Feature/issue-61-stripe-paypal | tasks: 9/9
     base: d9dcf0e | updated: 2026-10-31
     next: none (wrapped up) -->
# Plan: Stripe and PayPal (BE-057, #61)
## Tasks
- [x] Contract: ParseWebhookAsync, IncomingWebhook headers + raw body, WebhookParse notice kind/transaction id, PaymentVerification.Settlement; webhook endpoint reads raw body
- [x] Stripe adapter: Checkout Session create (idempotency key), Stripe-Signature HMAC + tolerance, session re-fetch with balance transaction, refund/dispute events
- [x] PayPal adapter: token cache, order create (payer-action link), capture on APPROVED, order re-fetch, verify-webhook-signature, refund/dispute events
- [x] Entities + migration (append-only, PG triggers): payment_settlements (unique per donation), provider_notices (unique per gateway event)
- [x] Processor: notice path (verified only, lookup, dedupe, record, warn), parse failures -> retry; settlement recorded on confirmation
- [x] Admin: provider-notices list; donation detail shows settlement; options + registration; per-currency default order
- [x] Docs (PAYMENT_GATEWAYS.md), openapi regen, map
- [x] Tests: both adapters create/verify/webhook via fakes, signature forgery/replay/tolerance, settlement distinct amounts, refund/dispute notices, currency routing, PG triggers
- [x] Full suite, format, OpenAPI verify, coverage check
## Tests
- High blast radius: fake-gateway integration incl. real HMAC; forged/replayed/stale signatures; PG trigger test
## Files
- src/Services/FinanceService/Payments/**, Data/**, Migrations/**, Donations/*, Program.cs; tests/**; docs/**; openapi.v1.yaml
