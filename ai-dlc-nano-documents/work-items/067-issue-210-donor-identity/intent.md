# Donor role, sponsorship permissions and donor accounts in Identity (BE-103)
- Source: issue #210 https://github.com/DevCastBD/KJWebsite.Backend/issues/210 (epic #208, decisions on #209)
- Type: feature (auth, high blast radius)
## Request
- Seed a donor role with no back-office permission, and the keys sponsorship.view, sponsorship.student.manage, sponsorship.link.manage, sponsorship.link.void (super admin only) and finance.donor.provision.
- POST /api/v1/admin/auth/donors with a staff-set initial password and a must-change flag; PUT .../password reset; PUT .../status (disable revokes sessions).
- POST /api/v1/auth/change-password (signed in) clears the flag; flag reported at sign-in and in /auth/me, which also reports roles.
- ACCOUNT_DISABLED (403) only after the correct password; every other failure stays generic.
## Decisions
- One person as member and donor (D7) -> one account: provisioning adds the donor role to an existing member account, keeps its password and sessions, sets no must-change flag
- New keys on existing databases -> a one-time backfill gives Admin the four non-void keys and records that it ran; link.void stays SuperAdmin only
- Must-change enforcement -> the API enforces it: only change-password, logout and /auth/me work until changed, else 403 PASSWORD_CHANGE_REQUIRED
- Password rules -> existing platform policy (minimum 10, same lockout); the new password must differ from the current one
## Out of scope
- Finance side of provisioning (#211), donor self-service (#212), students (#213), links (#214).
## Follow-ups
- Shared member and donor account: the donor password and status endpoints refuse it (409 ACCOUNT_SHARED); no way yet to switch off only the donor role
- Disabled dev seed account with its known password now answers 403 ACCOUNT_DISABLED in production (was 401); consider a generic answer for it
- DonorIdentity migration and its gated test were not run (no local Postgres)
