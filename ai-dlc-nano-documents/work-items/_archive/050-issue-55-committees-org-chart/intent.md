# Committees, chapters, terms and org chart (BE-048, #55)
- Source: issue #55 https://github.com/DevCastBD/KJWebsite.Backend/issues/55 (milestone 4 / P3)
- Docs: frontend admin contract src/admin/membership/api.ts + types.ts (GET|POST /committees, PUT|DELETE /committees/{id}, Committee {id,name,parentId,chair}); public page /about/committees has no data contract yet (static notice)
- Type: feature
## Request
- Committee (executive|advisory|project|chapter) with bilingual name, nesting, org chart
- CommitteeMember with position and explicit start/end dates; current vs historical, never deleted
- Public publication respects the member's name consent (#54)
## Decisions
- (approved) frontend sends only `name` + `chair`: `name` is accepted as nameEn (nameBn then falls back to it and is flagged `nameBnIsFallback`); `chair` is derived from the current "chair" term, so a draft's chair text is ignored (the UI needs a member picker)
- (approved) public output lists only current terms of consented, active members; a hidden member's seat is simply not shown; past terms only on the committee detail
- (approved) terms are ended, not deleted; committees with children or terms are archived, not deleted
## Out of scope
- Chapter assignment of members for the directory filter (follow-up once chapters exist), election workflows
## Follow-ups
