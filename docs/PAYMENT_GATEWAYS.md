# Payment gateways (BE-055, #60)

`FinanceService` starts a **hosted checkout** at a payment gateway and sends the donor there: card and wallet details never touch our services (ADR-008). Three adapters exist: **SSLCommerz** (cards and local wallets, BDT/USD/GBP/EUR), **bKash** (tokenized checkout, BDT) and **Nagad** (BDT). Stripe and PayPal (#61) will be further adapters.

> **Not verified against a live gateway.** No sandbox merchant credentials existed when this was built (see #111). Each adapter follows the gateway's published API and is tested against test doubles; Nagad's double performs the real RSA encrypt/sign round trip, which proves the adapter is self-consistent, not that it matches Nagad's service. Run each adapter against its sandbox before relying on it, and treat a field-level surprise there as a fix to that adapter alone.

## How a donation reaches a gateway
1. `POST /api/v1/donations` validates and saves the donation as `pending`.
2. The registry lists the adapters to try: if the request names a `gateway`, only that one (when it is on and supports the currency); otherwise every enabled adapter that supports the currency, in `Finance:Payments:Order` (default sslcommerz, bkash, nagad). First success wins.
3. Each try is recorded in `payment_attempts` (append-only): `created` with the gateway's payment id, or `failed` with a short error code (`GATEWAY_TIMEOUT`, `GATEWAY_UNREACHABLE`, `GATEWAY_HTTP_5xx`, `GATEWAY_BAD_RESPONSE`, `<GATEWAY>_REFUSED`, ...). Gateway messages are never stored or logged.
4. The response carries `gateway` and `checkoutUrl`. If nothing could be started (no gateway on, wrong currency, or all down) the donation is still `pending` and the response says `fallback: "offline"` so the site shows the offline-giving page. **A gateway outage never fails a donation.**

The charge is `amount + fee` (see DONATIONS.md). The gateway's order id is our donation `reference`.

## Webhooks and confirmation (BE-056, #62)
`GET|POST /api/v1/donations/webhooks/{gateway}` is where gateways call us. It is anonymous, so its safety is in the order of work:
1. **Authenticate.** SSLCommerz signs its IPN: `verify_sign` is the MD5 of the fields listed in `verify_key` plus `store_passwd` (the MD5 of the store password), sorted by name. A missing or wrong signature is rejected with `400`, recorded and logged, and **nothing is asked of the gateway**. bKash and Nagad do not sign their browser callbacks, so for them the next step is the only check.
2. **Find** the donation by the gateway's payment id (or SSLCommerz's `tran_id`, our reference). Unknown payments are rejected (`UNKNOWN_PAYMENT`).
3. **Re-query the gateway** with our credentials (`VerifyAsync`): SSLCommerz by transaction id, bKash by payment status (executing an `Authorized` payment once), Nagad by payment verification. The call that prompted this is only a trigger; **only this answer decides**. It must name the same reference and, for a paid payment, the exact charge (amount + fee) in the charged currency, otherwise the donation is left alone and `AMOUNT_MISMATCH` / `REFERENCE_MISMATCH` is recorded.
4. **Apply**: paid confirms through `DonationConfirmation` (event + ledger posting in one commit); failed or cancelled only moves a donation still `pending` (a late failure never undoes a confirmed one); pending changes nothing.
5. **Remember the delivery** (`payment_events`, unique on gateway + event key), after handling. A replay of the same delivery is recognised and skipped without calling the gateway; a replay under a new key re-verifies but cannot confirm twice (the posting's source is unique). Ten replays leave one confirmed event and one posting.
If the gateway cannot be reached the call answers `503` + `Retry-After` and is **not** remembered, so the gateway's redelivery is looked at; the donation stays `pending`. A donor's browser (GET) is always sent on to the site's status page (303), which shows the truth.

Rejections are the alert list: `webhook_rejections` (append-only; gateway, reason code, source address, reference; never the body) with `GET /api/v1/admin/finance/webhook-rejections`, and a Warning log line per rejection to hang a log alert on. Reasons: `BAD_SIGNATURE`, `UNKNOWN_GATEWAY`, `GATEWAY_DISABLED`, `UNKNOWN_PAYMENT`, `REFERENCE_MISMATCH`, `AMOUNT_MISMATCH`.

## Reconciliation
- **Pending resolver:** asks the gateway about every donation still `pending` more than `ReconcileAfterMinutes` (default 15) after its checkout started, and applies the answer like a webhook. Runs on a timer (`ReconcileIntervalMinutes`, default 10, 0 = off) and on demand via `POST /api/v1/admin/finance/reconciliation/pending/run` (`finance.reconcile`). A gateway that does not answer leaves the donation pending.
- **Payouts:** `POST /api/v1/admin/finance/payouts` imports a settlement `{gateway, payoutRef, paidOn?, lines:[{reference, gross, fee, net, currency}]}` (`gross = fee + net`). Each line is `matched` (donation confirmed, not reversed, via that gateway, not already settled, gross equal to the charge), `amount_mismatch`, `unmatched` (unknown, not confirmed or reversed) or `duplicate`. Matched lines post: Dr bank (net), Dr payment processing fees (fee), Cr the gateway's clearing account (gross), which clears what confirmation put there. The batch is written whole or not at all (a closed period rejects it), and a payout reference imports once. `GET .../reconciliation/unpaid` lists confirmed gateway donations no payout has settled. Bank statement matching is #70.

## Return is not confirmation
The gateway sends the donor back to `GET|POST /api/v1/donations/{reference}/return?result=success|fail|cancel` (SSLCommerz POSTs; the wallets GET). That endpoint reads nothing from the request and answers `303` to `{SiteBaseUrl}/{lang}/donate/status?ref=&gateway=&result=return|cancel`. It changes nothing: only the signed webhook (#62) will confirm payment, so a forged or abandoned return can never mark a donation paid.

## Configuration (`Finance:Payments`)
Credentials come from the environment or a secret store and are **never** committed: appsettings carries no credential keys (a test enforces it). Use double-underscore environment variables.

| Setting | Meaning |
|---|---|
| `PublicApiBaseUrl` | the API's public address, as the gateway reaches it (used for return URLs) |
| `SiteBaseUrl` | the public site the donor returns to |
| `Order` | preference order of gateway ids; empty means sslcommerz, bkash, nagad |
| `TimeoutSeconds` | per gateway call, default 10 |
| `SslCommerz:{Enabled,Sandbox,StoreId,StorePassword,BaseUrl}` | |
| `Bkash:{Enabled,Sandbox,Username,Password,AppKey,AppSecret,BaseUrl}` | token cached until a minute before expiry; one retry with a fresh token if bKash rejects it |
| `Nagad:{Enabled,Sandbox,MerchantId,MerchantPrivateKey,PgPublicKey,BaseUrl}` | keys as PEM or base64 DER |

An adapter is on only when `Enabled` is true **and** every credential is present; otherwise it is off and the donation falls back. `Sandbox` defaults to true; set it false only with live credentials. Sandbox mode also allows an `http://` checkout URL (some sandboxes use it); live mode requires https.

## Adding a provider
Write a class implementing `IPaymentGateway` (id, `IsEnabled`, `Supports`, `CreateCheckoutAsync`; throw `GatewayUnavailableException` or `GatewayRejectedException`) and register it with `AddPaymentGateway<T>(clientName)` in `Program.cs`. Donations, the registry, attempts, status and back office need no change (a test registers a provider from outside the adapter folder).
