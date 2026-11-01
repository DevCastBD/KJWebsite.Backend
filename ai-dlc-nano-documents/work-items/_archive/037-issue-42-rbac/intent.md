# BE-032 Full RBAC permission model and role management APIs (#42)
- Source: issue #42 https://github.com/DevCastBD/KJWebsite.Backend/issues/42 (milestone 3, order BE-032; closes G13)
- Type: feature
## Request
- Typed permission keys per module; role + permission assignment via API; last SuperAdmin cannot be removed; object-level ownership helper
## Decisions
- #108 (role counts) skipped by user: keep the plan's nine seeded roles, all editable except SuperAdmin (fixed = every permission)
- Seeder stops re-adding catalog permissions to non-SuperAdmin roles once they exist, so edits survive restarts
- "Audited": changes emitted as structured log events with before/after now; persisted audit log arrives with #43 (BE-033), which will hook these
- Role/user-role changes revoke the affected users' sessions so stale permission claims stop at once
## Out of scope
- Member self-service endpoints (P3); the ownership helper is provided and tested for them
## Follow-ups
