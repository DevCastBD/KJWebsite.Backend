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

# Campaign email (BE-077, #81)

Back office, `comms.manage`, under `/api/v1/admin/newsletter`.

- **Templates** (`/templates`) and **campaigns** (`/campaigns`) hold a bn and an en subject and body (light Markdown: paragraphs, `# heading`, `**bold**`, `[text](https://...)`; raw HTML is escaped). Variables: `{{email}}`, `{{unsubscribeUrl}}`, `{{preferencesUrl}}`; any other variable is rejected when the draft is saved, and a footer with the unsubscribe and preferences links is always added. A campaign copies its template, so editing a template does not change a draft. The legacy `email.html` is not in the repo, so no template is seeded yet (that is #82's catalogue).
- **Preview / test send**: `GET .../preview?lang=` and `POST .../test {email, lang}` use the same `NewsletterRenderer.Render` as the real send. A test message goes to one address, needs no confirmation and is prefixed `[TEST]`.
- **Send**: `POST .../send {confirmRecipientCount, scheduledAt?}`. Refused unless both languages are complete (400 `MISSING_LANGUAGE`) and `confirmRecipientCount` equals the current audience (409 `RECIPIENT_COUNT_MISMATCH`). The audience is the campaign's segment (or all confirmed subscribers); each recipient's rendered message is queued in their own language, spaced at `Newsletter:SendPerMinute` (default 120) so delivery is throttled. A campaign sends once; a sent campaign is frozen.
- **Delivery**: `NewsletterDispatcher` (background, every 5 s, `Newsletter:DispatchEnabled`) delivers due messages through `INewsletterTransport`. The default transport only logs, so no mail leaves until a real transport is registered. Consent is re-checked at delivery: a recipient who unsubscribed, bounced or complained since the send was queued is `skipped`.
- **Cancel** (`POST .../cancel`) cancels everything not yet delivered.
- **Report** (`GET .../report`): recipients, queued, sent, failed, skipped, cancelled, bounced, complaints, unsubscribed since the send began, and counts by language, from the outbox, so they add up.
- **Tracking**: none. No open pixel and no click rewriting; the report is delivery and consent outcomes only. Add it later only if the organisation decides it wants it.

# Transactional templates (BE-078, #82)

`/api/v1/admin/notifications/*`, `comms.manage`. The catalogue (`NotificationCatalogue`) lists nine messages (application received / status changed / approved, donation receipt, event registration, event reminder, dues reminder, password reset, admin alert), each with the variables it may use. All are seeded at startup as version 1 in **both** bn and en; the seeded wording is a starting point to edit.

- **Versioned and immutable**: `POST /templates/{key}/{lang}` saves an edit as the next version; the newest is the one sent. An edit may only use the key's declared variables.
- **Missing variable fails loudly**: `POST /templates/{key}/render {lang, values, version?, record?, toEmail?}` returns 400 `RENDER_ERROR` naming the variable if any variable the text uses has no value, so `{{name}}` never reaches a recipient. Language defaults to Bengali.
- **Reproducible past sends**: with `record: true` the rendered subject, text, HTML and the template version are stored (`GET /sends?key=`). Later edits never alter them, and `version` pins an old version for a re-render.
- **Mail-client-safe HTML**: one centred 600px table, inline styles, viewport meta, no `<style>`, flex, grid, web fonts, remote images or tracking; a test asserts this structure. Actual rendering in Gmail, Outlook and a phone client still needs a manual check with real accounts (not done here).
- Other services call the render endpoint (or move to a shared transport later); wiring each existing sender (Auth password reset, donation receipts, event notifications) onto it is a follow-up.
