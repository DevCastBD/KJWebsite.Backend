# Webhook handlers, replay protection, idempotency, reconciliation (BE-056)
- Source: issue #62 https://github.com/DevCastBD/KJWebsite.Backend/issues/62 (milestone 5, P4)
- Docs: docs/PAYMENT_GATEWAYS.md, docs/LEDGER.md, docs/IDEMPOTENCY.md
- Type: feature (high blast radius: money, public unauthenticated endpoints)
## Request
- Per-gateway webhook handlers; verify authenticity before anything else; reject unsigned/badly signed and alert.
- Replay x10 -> one confirmed donation + one ledger posting; timeout leaves donation pending, reconciliation resolves it.
- Payouts reconcile against recorded donations.
## Decisions
- Ledger dependency: #69 done first (user); reconciliation scope = pending resolver + payout matcher from imported settlement lines; bank statements stay #70 (user)
- Verification: signature where the gateway signs (SSLCommerz verify_sign), and ALWAYS a server-side status re-query via the adapter (new VerifyAsync); webhook/callback is only a trigger; confirm solely from the gateway's answer with matching reference, currency and charge (user)
- Rejections: append-only webhook_rejections table + admin list + Warning log (user)
- Payouts: admin JSON import; matcher (matched/unmatched/amount_mismatch/duplicate); matched lines post Dr bank net + Dr processing fees / Cr gateway clearing gross (user)
- Assumed: bKash/Nagad browser callbacks hit the webhook endpoint (which verifies, then 303s to the site); SSLCommerz IPN is server-to-server and its browser return stays on the return endpoint; bKash VerifyAsync executes an Authorized payment; replay key per gateway event in payment_events (recorded after handling, so a failed verify can be redelivered); new permission finance.reconcile (Admin only for now); pending resolver runs as admin endpoint + background worker (interval config, off in OpenAPI export)
## Out of scope
- Stripe/PayPal webhooks (#61), offline recording (#63), bank statement matching (#70)
## Follow-ups
- Webhook shapes (IPN fields, bKash status/execute, Nagad verify) follow published docs; verify against live sandboxes (#111)
- SSLCommerz signature uses MD5 because the gateway defines it
- Gateway refunds reconcile with the gateway's own refund record: #72
- Alerting is a table + Warning logs; hook a real alert when observability lands
