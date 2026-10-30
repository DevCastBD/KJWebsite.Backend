# Roles, permissions and ownership (BE-032, #42)

Endpoints require a **permission key**, never a role name (`Permissions` in BuildingBlocks; `PermissionCatalog` groups them by module with a description, served at `GET /api/v1/admin/auth/permissions`). A test keeps the catalog and the constants identical. Roles are bundles of keys, stored as ASP.NET Identity role claims, and the access token carries the flattened `permissions` claim.

## Seeded roles
Nine built-in roles (plan §3.1) are created on first start with the permissions in `KjRoles.Seed`. After that **the database is the truth**: a restart never re-adds a permission an administrator removed. `SuperAdmin` is the exception: it is re-synced to every catalog key at startup so it can never be locked out and new keys reach it at once. Existing deployments do not get new keys on other roles automatically; assign them through the API.

## Management API (`/api/v1/admin/auth`)
| Call | Permission |
|---|---|
| `GET /permissions`, `GET /roles`, `GET /roles/{name}`, `POST /roles`, `PUT /roles/{name}/permissions`, `DELETE /roles/{name}` | `roles.manage` |
| `GET /users?role=&limit=&offset=` | `users.view` |
| `PUT /users/{id}/roles` (replaces the set) | `users.manage` |

## Guards (all in `RoleAdministration`)
- `SuperAdmin` cannot be deleted or edited: `409 SUPERADMIN_PROTECTED`.
- The **last active SuperAdmin** cannot be removed or demoted: `409 LAST_SUPERADMIN` (disabled accounts do not count). Serialised in-process and in a serializable transaction.
- Only a SuperAdmin can grant or remove `SuperAdmin`.
- **No escalation:** you can only grant, or change, permissions you hold yourself (`403 FORBIDDEN`). Editing a role needs every permission it holds before or after.
- Built-in roles cannot be deleted (`SYSTEM_ROLE`); a role with members cannot be deleted (`ROLE_IN_USE`).
- Any role or user-role change **ends the affected users' sessions**, so a stale token cannot keep a revoked permission; they sign in again to get the new set.
- Every change is logged (`EventId 4101`) with actor, target and before/after. Persisting these in the immutable audit log is BE-033.

## Object-level ownership
A key says what a caller may do, not to whom. `ResourceOwnership.CanAccess(principal, ownerId, managerPermission)` is true for the owner or for a holder of the manager permission. Member self-service endpoints (P3) must call it: `member.profile.edit` lets a member edit **their own** profile; `membership.manage` lifts the restriction. Tested in `PermissionTests`.
