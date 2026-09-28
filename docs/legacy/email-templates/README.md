# Legacy email templates

Historical emails from the 2019 site, kept as **wording and structure to build from** — not templates to send as they are. Issue #14 (B0-10).

## `member-acceptance-2019.html`

The email sent to applicants accepted as members: *"Congratulations on being selected as a Member of Kolpojontro Foundation…"*, followed by an invitation to the **Projonmo Boron** orientation programme and its schedule.

### Provenance

- **Source:** `legacy-html/email.html` in the frontend repository (`DevCastBD/KJWebsite.FrontEnd`, last changed in `6791cdf`, copied at `5ead058`). It sat among the legacy web pages, where it looked like a missing route; it is a transactional email.
- **Changed on import — redactions only.** The frontend repository is private and this one is public, so personal details were replaced with markers. Everything else is byte-for-byte the original.

  | Marker | Replaces |
  |---|---|
  | `[PRESENTER]` | the names of two schedule presenters |
  | `[SIGNATORY]`, `[ROLE]` | the name and title of the officer who signed it |
  | `[PHONE]` | the contact phone number in the footer |

### What it seeds

| Future work | What to take from it |
|---|---|
| **P3 approval email** (#52) | The acceptance wording and tone — the organisation's own voice for the most important message it sends a new member. |
| **P6 transactional template catalogue** (#82) | The overall layout: header image, greeting, body, event block, sign-off, footer with the "why you're receiving this" line. |
| **P5 event agenda** | The schedule table — `#`, time, event, presenter — as the model for how an event's agenda is structured. |

### Update before reuse

Do **not** send this verbatim. Before it becomes a real template:

- **Dated event details.** The date (26 January 2019), reporting time, venue and the whole schedule belong to one past event. In a template they become fields.
- **The greeting.** *"Dear Concern"* is hard-coded, with no merge fields at all; it should address the member by name.
- **People and roles.** Presenters and the signatory are redacted here, but they would be out of date anyway — take them from current data, never from this file.
- **Contact details.** The original footer phone differs from the site's current number (`(880) 1977-887087`). Use the site's current contact details.
- **Dead links and images.** `kolpojontro.org` and `venue.kolpojontro.org` do not respond (checked 2026-09-29), and the two images — `kolpojontro.org/email-img/header.png` and `logo.png` — are in neither repository, so they are effectively lost and must be recreated. The domain itself is an open question: #121 in the frontend repository.
- **Plain `http://` links.** Every link and image uses `http://`; use HTTPS.
- **Wording errors.** *"Thanks, Giving Speech"* (a vote of thanks), *"Non-profitable"* (non-profit), and *"meet you in person"* after *"looking forward to"* (→ *meeting*).
- **No typo'd domain.** Unlike the legacy `success.php`, this file does not use the misspelt `kolpojantro.org`.

The frontend removes its copy as part of its A-10 cleanup (FrontEnd #22).
