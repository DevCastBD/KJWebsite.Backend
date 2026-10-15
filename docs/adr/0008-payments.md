# ADR-008: Payments via a provider abstraction, Bangladesh-first

- Status: Accepted
- Date: 2026-10-15
- Ratified by: Fahimul Hoque (@FHShubho)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12), [issue #111](https://github.com/DevCastBD/KJWebsite.Backend/issues/111)

## Context

Donations, event tickets and membership fees (P4) need online payment. Most donors pay with Bangladeshi mobile wallets, while diaspora donors pay by card. Merchant approval is slow and outside engineering's control, and which accounts the organisation can obtain, and who owns each application, is still open on #111.

## Decision

Integrate payments behind a single provider abstraction with one adapter per gateway. Target set, in priority order: bKash Merchant, Nagad and SSLCommerz (cards and local wallets) for Bangladesh; Stripe and PayPal for diaspora donors. Which adapters actually ship depends on the merchant accounts obtained under #111; the abstraction means a missing provider never blocks the others.

Use the hosted-checkout redirect flow so card and wallet credentials never touch our services. Webhook handlers must verify signatures, reject replays, and process idempotently. Donations are reconciled against gateway payouts. Build against sandbox credentials until live accounts exist, and ship offline-donation recording first so money already being received is accounted for.

## Rejected alternatives

- Hard-wiring one gateway: approval delays or a rejected application would block the whole phase, and it locks out one donor group.
- Collecting card details directly: brings PCI scope and security liability a volunteer project cannot carry.
- Stripe/PayPal only: excludes the wallets most Bangladeshi donors use.

## Consequences

- The merchant applications and their owners are tracked on #111; this ADR does not settle eligibility, fees or ownership.
- Adapters are added or dropped without changing donation, receipt or ledger code.
- Webhook endpoints are public and need signature checks, replay protection and tests from day one.
