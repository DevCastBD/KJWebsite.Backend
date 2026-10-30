# BE-031 Generalised ContentItem, taxonomy, redirects (#38)
- Source: issue #38 https://github.com/DevCastBD/KJWebsite.Backend/issues/38 (milestone 3, order BE-031)
- Type: feature
## Request
- Eight content types (project, news, page, faq, team-member, testimonial, partner, press) on one model/workflow
- Tags (Bengali-aware normalisation, one tag spans bn+en), related content across types
- Redirect table; slug change auto-creates a redirect; legacy .html/.php paths resolve
## Decisions
- Stacked on Feature/issue-48-... (user confirmed); autopilot
- Project/news endpoints unchanged (additive); the six new types share one generic public+admin API
## Out of scope
- Workflow/scheduling (BE-034), media (BE-036), search (BE-040)
## Follow-ups
