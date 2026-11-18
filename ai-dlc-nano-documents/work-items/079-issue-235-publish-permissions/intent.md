# Publish the user's permissions in the auth responses (BE-115, #235)
- Source: issue #235 https://github.com/DevCastBD/KJWebsite.Backend/issues/235 (part of epic #208; needed by FrontEnd#361)
- Type: enhancement (identity contract, additive)
## Request
- Add `user.permissions` (distinct, sorted keys from all the user's roles, same data as the token permissions claim) to login, refresh, change-password and GET /api/v1/auth/me.
- Describe the shapes in OpenAPI and docs; tests for every endpoint and role case.
## Decisions
- Chosen by the user on 2026-11-18 for FE#361: the backend publishes permissions, the frontend maps them (not a role-name map, not token decoding) → plan approved.
- Additive only: role, roles and all fields unchanged; empty list for Donor, Volunteer and roles without permissions.
## Out of scope
- Frontend changes (FE#361), new endpoints, changing the token.
## Follow-ups
- Not fixed: sign-in, refresh and change-password return tokens without Cache-Control: no-store (RFC 6749 5.1); add them to UsePrivateResponses.
- Not fixed: `role` is the first of GetRolesAsync, whose order is not guaranteed; clients should use `roles` and `permissions`.
