# BE-035 Translation completeness, missing-translation report, fallback rules (#40)
- Source: issue #40 https://github.com/DevCastBD/KJWebsite.Backend/issues/40 (milestone 3, order BE-035)
- Type: feature
## Request
- Completeness per item and per language; report of gaps across all content types; explicit, configured, documented fallback behaviour (decision needed, not a default)
## Decisions
- Fallback (user decided): bn requests get a placeholder flagged pending (other-language title, empty body), en requests get bn content flagged; configurable per language (hide|fallback|placeholder)
- Required fields per type live in code (ContentKinds); completeness is a warning on publish, enforceable with Content:Translations:RequireAllLanguages
## Out of scope
- Frontend side-by-side indicator (F2-10)
## Follow-ups
