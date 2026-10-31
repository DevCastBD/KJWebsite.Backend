# Payment gateway abstraction + bKash, Nagad, SSLCommerz adapters (BE-055)
- Source: issue #60 https://github.com/DevCastBD/KJWebsite.Backend/issues/60 (milestone 5, P4)
- Docs: docs/adr/0008-payments.md, docs/DONATIONS.md
- Type: feature
## Request
- Provider abstraction, one adapter per gateway, hosted-checkout redirect flow; adding a provider touches only its adapter.
- Credentials from environment/vault only; gateway outage degrades to the offline path, never fails the donation.
- Redirect return is never confirmation (that is #62).
## Decisions
- Adapters (SSLCommerz, bKash tokenized, Nagad) built to published API shapes, verified against fake HTTP only; docs flag "not verified against a live sandbox" (user)
- Optional `gateway` in POST /donations; default = configured order, first enabled adapter supporting the currency (user)
- No gateway / gateway down -> 201 pending, no checkoutUrl, `fallback: "offline"`; failed attempt recorded (user)
- Assumed: gateways return to a backend endpoint that 303-redirects to /{lang}/donate/status (SSLCommerz POSTs back; the SPA cannot take a POST); the return never changes status
- Assumed: failing candidate -> try next adapter only when donor did not pick one; credentials only via env/vault config, blank in appsettings
## Out of scope
- Webhooks/confirmation (#62), Stripe/PayPal (#61), offline recording (#63), merchant-account decisions (#111)
## Follow-ups
- Adapters unverified against live sandboxes; run each once credentials exist (#111)
- Webhook confirmation + provider payment id lookup: #62 (payment_attempts indexed by gateway+providerPaymentId)
- Several ~1ms tests failed once in separate full runs (PrivacyTests purge, AuthorizationTests former-secret, Finance totals); pass alone
