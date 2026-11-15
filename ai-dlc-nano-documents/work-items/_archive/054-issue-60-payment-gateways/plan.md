<!-- phase: DONE | branch: Feature/issue-60-payment-gateways | tasks: 8/8
     base: 253ee9d | updated: 2026-10-31
     next: none (wrapped up) -->
# Plan: Payment gateway abstraction + adapters (BE-055, #60)
## Tasks
- [x] Abstraction: IPaymentGateway, CheckoutRequest/Result, Unavailable/Rejected exceptions, registry (order, currency support, enabled only with credentials), options
- [x] Append-only payment_attempts entity + migration (+ PG trigger); admin `method` and public `gateway` read from it
- [x] SSLCommerz adapter (session init, form-encoded, GatewayPageURL)
- [x] bKash tokenized adapter (grant token cached, create payment, bkashURL)
- [x] Nagad adapter (initialize + complete with RSA encrypt/sign, Dhaka timestamps)
- [x] POST /donations: optional `gateway`, candidate loop, offline fallback, response gateway/checkoutUrl/fallback; return endpoint 303 -> /{lang}/donate/status (no status change)
- [x] Docs (docs/PAYMENT_GATEWAYS.md, DONATIONS.md), appsettings blanks, openapi regen, codebase-map
- [x] Tests (fakes): per adapter, selection/order/currency, outage fallback, return, credentials never leaked, add-provider-via-DI, replay = one checkout, PG trigger
## Tests
- High blast radius (money/secrets): fake-HTTP integration per adapter incl. real RSA round-trip for Nagad; full suite + format + OpenAPI --verify + PG
## Files
- src/Services/FinanceService/Payments/**, Donations/*, Data/**, Migrations/**; tests/**; docs/**; openapi.v1.yaml
