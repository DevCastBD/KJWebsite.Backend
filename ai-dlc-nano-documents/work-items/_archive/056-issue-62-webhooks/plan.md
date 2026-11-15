<!-- phase: DONE | branch: Feature/issue-62-webhooks | tasks: 10/10
     base: d243a57 | updated: 2026-10-31
     next: none (wrapped up) -->
# Plan: Webhooks, replay protection, reconciliation (BE-056, #62)
## Tasks
- [x] Adapter contract: ParseWebhookAsync (authenticity + ids + event key) and VerifyAsync (status query); CheckoutRequest gets WebhookUrl
- [x] SSLCommerz: ipn_url, verify_sign MD5 check, tran_id status query; bKash: query + execute when Authorized; Nagad: verify/payment query
- [x] Entities + migration (append-only, PG triggers): payment_events, webhook_rejections, payouts, payout_lines
- [x] WebhookProcessor: authenticate, find donation, verify (amount/currency/ref), confirm via DonationConfirmation or mark failed/cancelled, dedupe by event key, record rejections
- [x] Endpoints: GET|POST /api/v1/donations/webhooks/{gateway} (200 for IPN, 303 for browser callbacks); admin GET webhook-rejections
- [x] Pending resolver (service + admin run endpoint + background worker) for stale pending checkouts
- [x] Payouts: import endpoint, matcher, ledger posting of net/fee/gross, listing, unpaid-confirmed report; permission finance.reconcile
- [x] Docs (PAYMENT_GATEWAYS.md webhooks, LEDGER.md payout posting), openapi regen, map
- [x] Tests: per gateway signature/verify via fakes, forged/unsigned/mismatch rejected + logged, replay x10 = one confirmation + one posting, timeout stays pending then resolver confirms, payout matching cases + ledger, PG triggers
- [x] Full suite, format, OpenAPI verify, coverage check
## Tests
- High blast radius (public unauthenticated money endpoint): fake-gateway integration, replay/forgery/failure paths, PG trigger test
## Files
- src/Services/FinanceService/Payments/**, Reconciliation/**, Data/**, Migrations/**, Program.cs; BuildingBlocks Permissions; tests/**; docs/**; openapi.v1.yaml
