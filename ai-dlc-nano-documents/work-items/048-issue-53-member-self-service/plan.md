<!-- phase: DONE | branch: Feature/issue-53-member-self-service | tasks: 8/8
     base: 6056728 | updated: 2026-10-31
     next: none (done) -->
# Plan: Member self-service
## Tasks
- [x] campaigns/current: add state/id/name/opensOn/closesOn/nextOpensOn (own commit)
- [x] Member profile columns (PII protected), consent + consent log, notification prefs, profile changes, deletion requests, photo; fill at approval; migration
- [x] /api/v1/members/me: profile GET/PATCH (direct vs moderated), consents, notifications, photo, deletion-request
- [x] Admin moderation: GET profile-changes, POST approve/reject (membership.manage), audited
- [x] Auth: /members/me/sessions (+DELETE) and /auth/me/export; gateway routes
- [x] GET /members/me/export: profile, consents+log, application snapshot+history, changes, notices sent, audit rows, identity+sessions
- [x] Card: GET /members/me/card (json) and card.pdf (QuestPDF, Noto Sans Bengali, QR)
- [x] Tests (ownership both ways, defaults, moderation, export completeness, Bengali PDF), docs, openapi, PG, commits
## Tests
- High blast radius (PII, ownership): cross-member access, export completeness, consent defaults, PG
## Files
- MembershipService (Self/), AuthIdentityService, ApiGateway, Directory.Packages.props, font asset, tests, docs
