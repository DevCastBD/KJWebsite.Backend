# Member directory (BE-047, #54)
- Source: issue #54 https://github.com/DevCastBD/KJWebsite.Backend/issues/54 (milestone 4 / P3)
- Docs: frontend contract KJWebsite.FrontEnd src/api/endpoints/directory.ts (GET /api/v1/members/directory?q=&chapter=&page= -> {items[{id,name?,photoUrl?,expertise?}], total, pageSize, chapters}; 429 + Retry-After)
- Type: feature
## Request
- Public directory: only consented members and only consented fields, enforced in the SQL projection; withdrawal effective on the next request (no caching); paged + rate limited; no contact details
- Internal directory for staff: search/filter, no bulk contact details
## Decisions
- (approved) chapters: not modelled until #55; `chapters` is [] and `chapter` filter is accepted and ignored for now
- (approved) public photos are served only when scan status is "clean" (#41 will set it); until then photoUrl is never offered, since unscanned images may carry EXIF location
- (approved) internal directory = staff with member.profile.view; a members-only directory is a policy call for the core members
- (approved) search matches only fields the member consented to; order is by opaque id so order leaks nothing; page cap 50; gateway 60 searches / 10 min per IP
## Out of scope
- Chapters (#55), member lifecycle statuses (#57), public verification page
## Follow-ups
