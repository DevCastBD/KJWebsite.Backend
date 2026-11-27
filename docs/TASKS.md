# Task assignment (BE-074, #78)

In the Membership service (`src/Services/MembershipService/Tasks/`). Kept deliberately small: four statuses (`todo`, `in_progress`, `blocked`, `done`), one assignee, an optional due date, a `low/normal/high` priority. No custom fields, no workflow.

- **Assign** (`membership.manage`): `POST /api/v1/admin/membership/tasks` with `title`, `assigneeMemberId` (an active member), exactly one of `projectSlug` or `committeeId` (an active committee), optional `dueOn`, `priority`, `description`. `PUT .../tasks/{id}` edits or reassigns (fields left out stay), `DELETE` removes, `GET .../tasks?project=&committeeId=&assignee=&status=` lists.
- **Workload** `GET .../tasks/workload`: open tasks per member (todo, in progress, blocked), with overdue, due-soon (next 2 days) and high-priority counts, busiest first, so an overloaded volunteer shows up early. Done tasks are excluded.
- **Assignee**: `GET /api/v1/members/me/tasks` and `PUT /api/v1/members/me/tasks/{id}/status` (only their own tasks).
- **Notifications** go to the member's notification centre (`GET /api/v1/members/me/inbox`, `POST .../inbox/read`; the existing `/members/me/notifications` stays the notification preferences). They are written in the same commit as the change: `task.assigned` (on assign and on reassign), `task.due_changed`. `POST .../tasks/reminders/run` creates `task.due_soon` and `task.overdue` notices once per due date; run it daily from a timer (not wired yet). Email delivery is the outbox work (#32/#33).
