# Membership lifecycle (BE-050, #57)
- Source: issue #57 https://github.com/DevCastBD/KJWebsite.Backend/issues/57 (milestone 4 / P3); #56 skipped (needs #109)
- Docs: plan §3.4 Member.Status active|inactive|on_leave|alumni|terminated; no frontend contract exists (admin Members shows only active/lapsed standing)
- Type: feature
## Request
- Transitions: leave of absence, alumni, termination with reason, reactivation; every one audited, history kept
- Tenure correct across interrupted service
- Termination reasons restricted; status drives directory/consent
## Decisions
- (approved) state machine: active>{on_leave,inactive,alumni,terminated}; on_leave/inactive>{active,alumni,terminated}(+each other no); alumni>{active,terminated}; terminated>{active}. Same-state refused. effectiveOn defaults today, never in the future, never before the previous effective date
- (approved) tenure = time spent `active` (leave, inactive, alumni, terminated do not count): periods list + totalDays; years/months are derived (365.25/30.44)
- (approved) permissions: `membership.lifecycle` (all non-terminal moves, MembershipOfficer) and `membership.terminate` (terminate, reactivate a terminated member, read termination reasons; Admin/SuperAdmin only)
- (approved) terminating disables the Identity login (and ends sessions); reactivating restores it. alumni/terminated/inactive end open committee terms on the effective date; terminated withdraws all publication consent (logged)
- (approved) alumni appear in the public directory only after a separate opt-in `alumni` consent; on_leave/inactive/terminated never appear
## Out of scope
- Dues-driven lapse (#56), automatic status changes
## Follow-ups
