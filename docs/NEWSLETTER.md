# Newsletter list (BE-076, #80)

Lives in ContentService (`content` schema). Back-office permission: `comms.manage`.

## Flow
1. `POST /api/v1/newsletter/subscribe` `{email, lang?, topics?, source?, website?}` always answers 202 for a valid address (no enumeration). A new or previously-unsubscribed address becomes `pending` and a bilingual confirmation message is queued in `newsletter_messages` (delivery is #81). Confirmed and suppressed addresses get nothing; a pending address is re-sent at most every 10 minutes. `website` is a honeypot.
2. `POST /api/v1/newsletter/confirm` `{token}` makes it `confirmed`. POST so mail scanners that open links cannot confirm. The confirm event is stored as consent evidence (`newsletter_events`).
3. Campaign messages go through `NewsletterStore.EnqueueCampaignAsync`, which refuses anything but `confirmed` and attaches the per-recipient `ListUnsubscribeUrl` (send as `List-Unsubscribe` plus `List-Unsubscribe-Post: List-Unsubscribe=One-Click`).

## Links
Confirm and manage tokens are `<subscriberId>.<HMAC>` signed with `Newsletter:LinkKey` (required outside Development, 32+ chars). No login, nothing stored, purpose-separated.

- `POST /api/v1/newsletter/unsubscribe/{token}`: RFC 8058 one-click, no confirmation, idempotent.
- `GET|PUT /api/v1/newsletter/preferences/{token}`: language (bn default, en) and topics; the address is masked.

## Bounces and complaints
`POST /api/v1/newsletter/provider-events` with `X-Webhook-Secret` (`Newsletter:WebhookSecret`; 404 while unset): `[{email, type}]`, type `bounce` or `complaint` (suppress at once) or `soft-bounce` (suppress on the 3rd). A suppressed address stays suppressed, including if the person signs up again.

## Segments
`POST /api/v1/admin/newsletter/segments` saves a filter `{langs, sources, topics, attendedEvent}`; `GET .../segments/{id}/subscribers` resolves it to confirmed subscribers in a fixed order (by address), so the same data gives the same list. `attendedEvent` matches checked-in event registrations by the registration email hash. Member and donor segments arrive as `source` values when those services push addresses through the normal double opt-in.

## Config
`Newsletter:LinkKey`, `Newsletter:WebhookSecret`, `Newsletter:SiteUrl` (confirmation link), `Newsletter:ApiUrl` (unsubscribe URL).
