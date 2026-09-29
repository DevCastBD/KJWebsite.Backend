# legacy/ — read-only historical snapshots

> [!WARNING]
> **Read-only. Never build, run, deploy or copy code from these folders.** They are here as a record of the 2019 registration system, to mine for its domain model — not as a codebase.

Moved here from the repository root by issue #13 (B0-9). Before that they were git-subtree imports (`0f70275`, `32afd13`, 2026-05-27) of the original 2019 projects.

| Folder | What it is |
|---|---|
| `kj-registration/` | The 2019 ASP.NET Core registration app (`Kolpojontro.Reg`) with ASP.NET Identity and MySQL. |
| `kj-registration-AwaitingUserFeatures/` | **Identical to the above except for one file** (`kj-registration/` also has a one-line `README.md`). It is a duplicate, not a later stage of the project. |

Neither is part of `KJWebsite.Backend.slnx`, and the root `Directory.Build.props` / `Directory.Packages.props` skip any project whose path contains `kj-registration`, so they cannot pick up the current build settings or package versions. Their Visual Studio caches (`.vs/`) and build output (`bin/`, `obj/`) were removed during the move and are git-ignored here.

## What to mine

These define the **membership domain** the new backend has to reproduce (P3). Take them from the code, not from memory:

**Applicant workflow** — `Kolpojontro.Reg/Core/Models/EAwaitingUserStatus.cs`

| Value | Status |
|---|---|
| 0 | `Awaiting` |
| 1 | `Approved` |
| 2 | `Rejected` |
| 3 | `Posponed` — **misspelt** in the source; the new model should say `Postponed` |
| 4 | `Terminated` |

**Roles** — seeded in the `aspnetroles` table: `SuperAdmin`, `Admin`, `Member`. (A `Moderator` role is mentioned in planning notes but does **not** exist in this code or data.)

**Applicant fields** — `Core/Models/AwaitingUser.cs`:
`FirstName`, `LastName`, `Gender`, `ReasonForJoining`, `PresentOrganization`, `VolunteeingExperience`, `DateOfBirth`, `DOB`, `CityOfResidence`, `CountryOfResidence`, `Status`, `StatusLastUpdatedAt`

**Member profile fields** — `Core/Models/ApplicationUser.cs`, the applicant fields plus:
`PermanentAddress`, `MailingAddress`, `IsMailingAddressSameAsPermanentAddress`, `BloodGroup`, `AreasOfExpertise`, `HighestDegree`, `DisabilitiesIfAny`, `Nationality`, `PersonalWebPage`, `SocialMediaLink`, `Roles`

Worth knowing before mapping them: `VolunteeingExperience` is misspelt, and the current `AuthIdentityService` carries the same spelling through its contracts, entity and **database migration** — so correcting it now means a schema migration, not just a rename. And **date of birth is stored twice** — `DateOfBirth` as a string and `DOB` as a datetime — which the new model should not repeat.

## What never to run or copy

- **The applications.** They target `netcoreapp2.2` (end-of-life since 2019) with a MySQL-backed ASP.NET Identity setup this repository does not use, and nothing here is maintained.
- **The authentication code** (`Security/Hashing/PasswordHasher.cs`, `Services/AccountService.cs`). It is a hand-rolled hasher — PBKDF2 with **1,000 iterations**, far below current guidance — layered beside ASP.NET Identity. The current services must use the framework's hashing, never this.
- **The MySQL dumps** (`kolpojontro_dev_*.sql`). Do not import them into any database or seed data from them.

## The MySQL dumps — checked

Issue #13 asked for the dumps to be checked for real personal data before leaving them in a public repository.

**Result (reviewed 2026-09-29): test data only.** Three unique dumps, each committed in both folders, holding 10 person rows in total:

- applicants named `TestFirst`/`TestSecond`, placeholder surnames such as *"New User 1 Last"* with reasons like *"Testing 1"*, and auto-generated lorem-ipsum records;
- two user accounts — the development seed accounts `admin@admin.com` and `user1@kol.com` — with empty phone numbers.

No real person appears, so the dumps stay and no history rewrite was needed. The legacy configuration points at a local development database (`localhost`, `Kolpojontro_Dev`) with an empty password, so no real credential is committed either.
