# Roles, permissions and ownership (BE-032, #42)

Endpoints require a **permission key**, never a role name (`Permissions` in BuildingBlocks; `PermissionCatalog` groups them by module with a description, served at `GET /api/v1/admin/auth/permissions`). A test keeps the catalog and the constants identical. Roles are bundles of keys, stored as ASP.NET Identity role claims, and the access token carries the flattened `permissions` claim.

## Seeded roles
Ten built-in roles (plan §3.1) are created on first start with the permissions in `KjRoles.Seed`. After that **the database is the truth**: a restart never re-adds a permission an administrator removed. `SuperAdmin` is the exception: it is re-synced to every catalog key at startup so it can never be locked out and new keys reach it at once. Except for the one-time `DonorIdentity` migration below, existing deployments do not get new keys on other roles automatically; assign them through the API.

`Donor` is seeded with no permissions. BE-103 (#210; decisions #209 D6, D7, D9) adds:
- `finance.donor.provision`: give a donor a sign-in, set a new initial password, disable or re-enable it.
- `sponsorship.view`.
- `sponsorship.student.manage`.
- `sponsorship.link.manage`.
- `sponsorship.link.void`: remove a mistaken link; SuperAdmin only.

`Admin` holds the four non-void keys. A fresh database gets them from the seed; the one-time `DonorIdentity` migration gives them to an already-seeded `Admin` role. A later removal through the role API is not undone.

## Management API (`/api/v1/admin/auth`)
| Call | Permission |
|---|---|
| `GET /permissions`, `GET /roles`, `GET /roles/{name}`, `POST /roles`, `PUT /roles/{name}/permissions`, `DELETE /roles/{name}` | `roles.manage` |
| `GET /users?role=&limit=&offset=` | `users.view` |
| `PUT /users/{id}/roles` (replaces the set) | `users.manage` |
| `POST /donors`, `PUT /donors/{userId}/password`, `PUT /donors/{userId}/status` | `finance.donor.provision` |

The donor endpoints are in Identity:
- `POST /donors` creates an email-confirmed `Donor` account with a staff-chosen initial password under the platform password policy (minimum 10 characters) and the must-change flag set. It is idempotent per donor id and never returns the password. If the email belongs to a `Member` account, it adds the `Donor` role and leaves the password, sessions and flag untouched. Any other existing account returns `409 EMAIL_TAKEN`.
- `PUT /donors/{userId}/password` sets a new initial password under the same policy, sets the must-change flag, clears a lockout and ends every session.
- `PUT /donors/{userId}/status` sets the account active or disabled; disabling ends every session.
- Both `PUT` endpoints refuse an account that is not donor-only (`409 ACCOUNT_SHARED`), because membership staff own a member account. The member status and erase endpoints skip donor-created accounts (`404`).

## Guards (all in `RoleAdministration`)
- `SuperAdmin` cannot be deleted or edited: `409 SUPERADMIN_PROTECTED`.
- The **last active SuperAdmin** cannot be removed or demoted: `409 LAST_SUPERADMIN` (disabled accounts do not count). Serialised in-process and in a serializable transaction.
- Only a SuperAdmin can grant or remove `SuperAdmin`.
- **No escalation:** you can only grant, or change, permissions you hold yourself (`403 FORBIDDEN`). Editing a role needs every permission it holds before or after.
- Built-in roles cannot be deleted (`SYSTEM_ROLE`); a role with members cannot be deleted (`ROLE_IN_USE`).
- Any role or user-role change **ends the affected users' sessions**, so a stale token cannot keep a revoked permission; they sign in again to get the new set.
- Every change is logged (`EventId 4101`) and written to the immutable audit log (`IdentityUserRole`, `IdentityRoleClaim`, `AuthRole` rows, see `docs/AUDIT_LOG.md`), read at `GET /api/v1/admin/auth/audit` with `audit.view`.

## Object-level ownership
A key says what a caller may do, not to whom. `ResourceOwnership.CanAccess(principal, ownerId, managerPermission)` is true for the owner or for a holder of the manager permission. Member self-service endpoints (P3) must call it: `member.profile.edit` lets a member edit **their own** profile; `membership.manage` lifts the restriction. Tested in `PermissionTests`.
