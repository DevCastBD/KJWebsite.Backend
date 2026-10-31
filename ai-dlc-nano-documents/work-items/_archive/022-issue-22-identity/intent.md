# Adopt ASP.NET Core Identity (#22, closes #15)
- Source: issue #22 https://github.com/DevCastBD/KJWebsite.Backend/issues/22 (also closes G1 #15)
- Docs: docs/MASTER_PLAN.md §4.2 ADR-002, §5 B1-2, §7; docs/adr/0002-identity.md
- Type: feature (security)
## Request
- Replace hand-rolled users/passwords with ASP.NET Core Identity (hashing, lockout, email confirmation, password reset).
- Plain-text `Password` column is dropped, never converted; existing users must reset.
- Dev seed admin works only in Development.
- Legacy profile columns move to a `member_profiles` table (auth schema, interim home for Membership).
## Decisions
- Part of a stacked run of #22→#23→#24→#25; #15-#18 closed by their fix; #19/#20 already merged (user confirmed).
- Assumed: register ignores requested roles (public self-assign of admin was a hole); new users get Member.
- Assumed: login requires confirmed email (config `Identity:RequireConfirmedEmail`, default true); dev seed admin is pre-confirmed.
- Assumed: email goes through an `IAuthEmailSender` abstraction; Development default logs the link, real delivery is #32.
- Assumed: lockout 5 attempts / 15 min, configurable; locked → 423.
## Out of scope
- Full role set and permissions (#24), JWT (#23), MFA, rate limiting (#28).
## Follow-ups
