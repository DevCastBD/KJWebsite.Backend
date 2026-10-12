# Kolpojontro Foundation — Backend Master Plan

> **This document is the single source of truth for backend development.**
> Any backend work not described here — or not added here first — is out of scope.
> Phase numbers are shared with [`FRONTEND_MASTER_PLAN.md`](FRONTEND_MASTER_PLAN.md) so the two tracks
> can be built and released in parallel or in sequence. Phase 0 for both is defined in
> [`MASTER_PLAN.md`](MASTER_PLAN.md).

| | |
|---|---|
| **Repository** | `KJWebsite.Backend` — github.com/DevCastBD/KJWebsite.Backend |
| **Runtime** | .NET 10 (`net10.0`), ASP.NET Core Minimal APIs, EF Core 10 |
| **Contract** | `openapi.v1.yaml` |
| **Status** | Draft v1 — awaiting architecture sign-off (see [ADR queue](#42-adr-queue)) |
| **Consumers** | Public website, member portal, back-office dashboard, future mobile app |

---

## Table of contents

1. [Current state and gap analysis](#1-current-state-and-gap-analysis)
2. [Target architecture](#2-target-architecture)
3. [Domain model](#3-domain-model)
4. [Engineering standards](#4-engineering-standards)
5. [Phase plan](#5-phase-plan)
   - [P0 — Foundation hygiene](#p0--foundation-hygiene-no-frontend-dependency)
   - [P1 — Production-grade platform core](#p1--production-grade-platform-core)
   - [P2 — Content platform and back-office core](#p2--content-platform-and-back-office-core)
   - [P3 — Membership and volunteer lifecycle](#p3--membership-and-volunteer-lifecycle)
   - [P4 — Donations and financial management](#p4--donations-and-financial-management)
   - [P5 — Events, programmes and volunteer operations](#p5--events-programmes-and-volunteer-operations)
   - [P6 — Communications, community and press](#p6--communications-community-and-press)
   - [P7 — Reporting, analytics and transparency](#p7--reporting-analytics-and-transparency)
   - [P8 — Scale, hardening and platform maturity](#p8--scale-hardening-and-platform-maturity)
6. [Non-functional requirements](#6-non-functional-requirements)
7. [Security and compliance](#7-security-and-compliance)
8. [Environments, deployment and operations](#8-environments-deployment-and-operations)
9. [Frontend alignment matrix](#9-frontend-alignment-matrix)
10. [Risks](#10-risks)
11. [Open decisions](#11-open-decisions)

---

## 1. Current state and gap analysis

### 1.1 What exists

| Component | State |
|---|---|
| `KJWebsite.Backend.slnx` | Solution with 5 projects |
| `src/Services/ApiGateway` (:7000) | YARP reverse proxy; routes `/api/v1/content/*`, `/api/v1/cta/*`, `/api/v1/auth/*`, `/api/v1/admin/content/*`. Routes and destinations are **hard-coded in `Program.cs`** as `localhost` addresses |
| `src/Services/ContentService` (:7001) | Projects + news, public read and admin write, bn/en translations. **In-memory `List<T>` — no database.** Admin auth is the literal string `dev-admin-token` |
| `src/Services/CtaSubmissionService` (:7002) | `POST /api/v1/cta/submissions` only. SQLite + EF Core. Stores raw `values` JSON plus extracted legacy profile columns |
| `src/Services/AuthIdentityService` (:7003) | Register / login / refresh / logout / me. SQLite + EF Core. Seeds `admin@site.org` / `admin123` |
| `src/BuildingBlocks/KJWebsite.BuildingBlocks` | Empty (`Class1.cs`) |
| `openapi.v1.yaml` | 638-line aggregated contract |
| `kj-registration/`, `kj-registration-AwaitingUserFeatures/` | Read-only legacy ASP.NET Identity snapshots. Source of the membership domain: `AwaitingUser` with statuses `Awaiting / Approved / Rejected / Postponed / Terminated`, roles `SuperAdmin / Admin / Moderator / Member`, and the full member profile field set |

### 1.2 Gap analysis

Severity: 🔴 blocks production · 🟠 blocks a planned feature · 🟡 quality/maintainability

| # | Gap | Sev. | Phase |
|---|---|---|---|
| G1 | Passwords stored in plain text (`AuthUserEntity.Password`, compared with `u.Password == payload.Password`) | 🔴 | P1 |
| G2 | Access tokens are opaque GUIDs in a process-local `Dictionary` — every restart logs everyone out, and horizontal scaling is impossible | 🔴 | P1 |
| G3 | `dev-admin-token` hard-coded as the content-admin credential | 🔴 | P1 |
| G4 | ContentService has no persistence — content vanishes on restart | 🔴 | P1 |
| G5 | No CORS policy — the browser frontend cannot call the API at all from another origin | 🔴 | P1 |
| G6 | No rate limiting on public write endpoints (`/cta/submissions`, `/auth/login`, `/auth/register`) | 🔴 | P1 |
| G7 | SQLite in a multi-service topology; no PostgreSQL provider or production data story | 🔴 | P1 |
| G8 | No tests of any kind | 🔴 | P0/P1 |
| G9 | No Docker, no CI/CD, no deployment target | 🔴 | P0/P1 |
| G10 | No structured logging, metrics, tracing, or health-check aggregation | 🟠 | P1 |
| G11 | No email delivery — registrations and contact submissions are silently swallowed | 🟠 | P1 |
| G12 | No file/media storage — content cannot carry real images | 🟠 | P2 |
| G13 | No RBAC beyond a two-value `Role` string; no permission model | 🟠 | P2 |
| G14 | No audit trail on any mutation | 🟠 | P2 |
| G15 | CTA submissions are write-only — nobody can read, triage, or export them | 🟠 | P2 |
| G16 | No membership lifecycle (the legacy system's core capability was lost) | 🟠 | P3 |
| G17 | No donations, payments, receipts, or any financial domain | 🟠 | P4 |
| G18 | No events, attendance, or volunteer-hours tracking | 🟠 | P5 |
| G19 | No newsletter, notifications, or campaign email | 🟠 | P6 |
| G20 | No reporting or analytics aggregation | 🟠 | P7 |
| G21 | Gateway configuration is code, not config; no per-environment routing | 🟡 | P1 |
| G22 | `BuildingBlocks` is empty — every service re-implements `ApiError`, `NormalizeLang`, auth checks | 🟡 | P1 |
| G23 | No input validation framework; ad-hoc `if` checks per endpoint | 🟡 | P1 |
| G24 | Error shape (`{ error: { code, message } }`) is custom and inconsistent with RFC 9457 Problem Details | 🟡 | P1 |
| G25 | ContentService response shape (`{ items: [...] }` filtered by `?lang=`) does not match what the frontend `contentService.js` expects (`{ projects: { bn, en } }`) | 🟠 | P1 |
| G26 | No API versioning strategy beyond the `/v1/` path segment | 🟡 | P1 |
| G27 | No idempotency support on write endpoints (double-submitted forms create duplicates) | 🟡 | P2 |
| G28 | No background job runner or outbox — email/webhook side effects would run inline in request threads | 🟠 | P1 |
| G29 | No backup, restore, or disaster-recovery procedure | 🔴 | P1 |
| G30 | No data-retention or privacy policy for personal data collected from applicants | 🟠 | P3 |

---

## 2. Target architecture

### 2.1 Deployment topology (ADR-001 accepted)

**[ADR-001](adr/0001-true-microservices.md): retain independently deployable microservices behind the existing YARP gateway.** The organisation may need to host applications on different domains or co-host them later. Service boundaries preserve independent deployment in either arrangement; gateway routing controls the public domains.

```
   Browser (static SPA)
           │
           ▼
   ApiGateway (YARP) ── routing, CORS, rate limits, auth pre-check
           │
           ├── Identity service ────┐
           ├── Content service ─────┤
           ├── Engagement/CTA ──────┤── PostgreSQL (schema per service)
           └── future services ─────┘
```

Each service owns its folder, EF `DbContext`, migrations, and **PostgreSQL schema**. Services communicate through authenticated APIs or events, never by writing another service's tables. Shared contracts and utilities live in versioned `BuildingBlocks` packages. P1 includes service-to-service authentication, a service registry for discovery, and distributed tracing from day one. Workers, Redis, and object storage can be added as needed. Deploy services together on shared infrastructure or separately without changing their ownership boundaries.

The rejected modular monolith would have reduced deployables and operational work. True microservices add network hops, cross-service failure modes, and more migrations and deployment work. Budget roughly +30 % effort per phase against that alternative; monitor hosting and support costs as services are added.

### 2.2 Module responsibilities

| Module | Owns | Public surface | Back-office surface |
|---|---|---|---|
| **Identity** | Users, credentials, sessions, roles, permissions, MFA | Login, register, refresh, password reset, `me` | User & role administration, impersonation audit |
| **Content** | Projects, news, events content, pages, FAQ, team, testimonials, partners, press coverage, media library, navigation, translations | Read APIs, sitemap/RSS feeds, search | Full CRUD, drafts, scheduling, publishing workflow |
| **Engagement** | Contact/CTA submissions, newsletter subscriptions, enquiries | Submit endpoints | Inbox, triage, assignment, export |
| **Membership** | Applications, approvals, member profiles, committees, dues | Apply, member self-service portal | Application pipeline, member directory, roles, ID cards, dues |
| **Finance** | Donations, campaigns, payments, receipts, expenses, budgets, ledger, donors | Donate, campaign progress, receipt download | Ledger, reconciliation, expense approval, financial reports |
| **Events** | Events, sessions, registrations, attendance, volunteer hours, tasks | Event listing, RSVP | Event management, attendance capture, hour approval |
| **Communications** | Email/SMS templates, campaigns, notifications, subscriptions | Unsubscribe, preference centre | Campaign composer, send, delivery reports |
| **Reporting** | Aggregates, dashboards, impact metrics, exports, audit log | Public impact/transparency figures | Dashboards, custom reports, scheduled exports |
| **Platform** (BuildingBlocks) | Errors, validation, paging, auth primitives, outbox, caching, storage, localisation, audit, feature flags | — | — |

---

## 3. Domain model

Notation: `PK` primary key, `FK` foreign key. All tables carry `Id` (ULID/GUIDv7), `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, and soft-delete `DeletedAt` unless noted.

### 3.1 Identity & access (P1–P2)

```
User            Id, Email(unique), NormalizedEmail, PasswordHash, SecurityStamp,
                EmailConfirmed, PhoneNumber, PhoneConfirmed, TwoFactorEnabled,
                LockoutEnd, AccessFailedCount, Status(active|suspended|deleted),
                PreferredLanguage(bn|en), LastLoginAt
Role            Id, Name(SuperAdmin|Admin|Moderator|FinanceOfficer|ContentEditor|
                EventManager|MembershipOfficer|Member|Volunteer), Description, IsSystem
Permission      Id, Key (e.g. content.publish, finance.expense.approve), Description
RolePermission  RoleId FK, PermissionId FK
UserRole        UserId FK, RoleId FK, AssignedAt, AssignedBy, ExpiresAt?
RefreshToken    Id, UserId FK, TokenHash, DeviceInfo, IpAddress, CreatedAt,
                ExpiresAt, RevokedAt, ReplacedByTokenId
PasswordReset   Id, UserId FK, TokenHash, ExpiresAt, UsedAt
AuditLog        Id, ActorUserId, Action, EntityType, EntityId, Before(jsonb),
                After(jsonb), IpAddress, UserAgent, OccurredAt
```

Migration note: `AuthUserEntity`'s inline legacy profile columns (`FirstName`…`SocialMediaLink`) move to `MemberProfile` in the Membership module. `User` keeps only identity concerns.

### 3.2 Content (P2)

```
ContentItem       Id, Type(project|news|page|event|faq|testimonial|
                  team_member|partner|press_coverage), Slug(unique per type),
                  Status(draft|in_review|scheduled|published|archived),
                  PublishedAt, ScheduledFor, AuthorId FK, FeaturedImageId FK,
                  SortOrder, IsFeatured, Metadata(jsonb)
ContentTranslation Id, ContentItemId FK, Language(bn|en), Title, ShortTitle,
                  Summary, Body(markdown/html), SeoTitle, SeoDescription,
                  OgImageId FK
Project           ContentItemId PK/FK, Category, StartYear, EndYear,
                  ProjectStatus(active|completed|planned|paused),
                  Icon, BeneficiaryCount, LocationId FK, BudgetTargetAmount
NewsArticle       ContentItemId PK/FK, ExternalSourceName, ExternalSourceUrl,
                  IsPressCoverage, PublicationDate
Tag / ContentTag  Taxonomy for filtering and related content
MediaAsset        Id, StorageKey, MimeType, SizeBytes, Width, Height,
                  AltText(bn/en), Caption, Checksum, UploadedBy, Variants(jsonb)
Collaborator      Id, ProjectId FK, Name, LogoId FK, Url, Role
Redirect          Id, FromPath, ToPath, StatusCode — preserves legacy URLs
```

### 3.3 Engagement (P1–P2)

```
Submission        Id, FormName, CtaType, Language, SourcePath, Values(jsonb),
                  SubmittedAt, IpHash, UserAgent, SpamScore,
                  Status(new|in_progress|responded|spam|closed),
                  AssignedToUserId FK, RespondedAt, InternalNotes
NewsletterSub     Id, Email, Name, Language, Status(pending|confirmed|
                  unsubscribed|bounced), ConfirmToken, ConfirmedAt,
                  UnsubscribedAt, Source
```

### 3.4 Membership (P3)

```
MembershipApplication Id, ApplicantEmail, ApplicantPhone, SubmittedAt,
                  Status(awaiting|under_review|shortlisted|interview_scheduled|
                  approved|rejected|postponed|withdrawn|terminated),
                  StatusChangedAt, StatusChangedBy, ReviewerNotes, Campaign(ProjonmoSeason),
                  Score, ProfileSnapshot(jsonb)
ApplicationEvent  Id, ApplicationId FK, FromStatus, ToStatus, ActorId, Note, OccurredAt
Member            Id, UserId FK, MemberCode(unique, e.g. KJ-2026-0042),
                  JoinedAt, Type(volunteer|general|life|honorary|advisor),
                  Status(active|inactive|on_leave|alumni|terminated),
                  ChapterId FK, ExitedAt, ExitReason
MemberProfile     MemberId PK/FK, FirstName, LastName, Gender, DateOfBirth,
                  BloodGroup, Nationality, PresentOrganization, HighestDegree,
                  AreasOfExpertise, VolunteeringExperience, ReasonForJoining,
                  DisabilitiesIfAny, PersonalWebPage, SocialMediaLinks(jsonb),
                  PermanentAddress, MailingAddress, IsMailingSameAsPermanent,
                  CityOfResidence, CountryOfResidence, EmergencyContact, PhotoId FK,
                  ConsentToPublish, ConsentGivenAt
Committee         Id, Name(bn/en), Type(executive|advisory|project|chapter),
                  ParentId FK, Description, IsActive
CommitteeMember   Id, CommitteeId FK, MemberId FK, Position, TermStart, TermEnd
MembershipDue     Id, MemberId FK, PeriodStart, PeriodEnd, AmountDue, Currency,
                  Status(pending|paid|waived|overdue), PaymentId FK, WaivedReason
```

Legacy mapping: `EAwaitingUserStatus.{Awaiting, Approved, Rejected, Posponed, Terminated}` → `MembershipApplication.Status`, with the typo corrected and three new intermediate states added. `ERoles.{SuperAdmin, Admin, Moderator, Member}` → seeded `Role` rows.

### 3.5 Finance (P4)

```
Campaign          Id, ContentItemId FK, GoalAmount, Currency, StartDate, EndDate,
                  Status(draft|active|paused|completed), IsRecurringAllowed
Donation          Id, DonorId FK, CampaignId FK, ProjectId FK, Amount, Currency,
                  Type(one_time|recurring|in_kind|pledge), Channel(bkash|nagad|
                  sslcommerz|stripe|paypal|bank_transfer|cash|cheque),
                  Status(initiated|pending|succeeded|failed|refunded|cancelled),
                  IsAnonymous, DedicationNote, ReceiptId FK, RecordedBy,
                  ExternalTransactionId, RawGatewayPayload(jsonb)
Donor             Id, UserId FK?, Type(individual|organisation), Name, Email,
                  Phone, Address, TaxId, IsAnonymous, TotalDonatedCached,
                  FirstDonationAt, LastDonationAt, CommunicationConsent
RecurringDonation Id, DonorId FK, Amount, Interval(monthly|quarterly|yearly),
                  NextChargeAt, Status, GatewaySubscriptionId, CancelledAt
Receipt           Id, DonationId FK, ReceiptNumber(unique, sequential),
                  IssuedAt, PdfAssetId FK, EmailedAt
PaymentAttempt    Id, DonationId FK, Provider, ProviderRef, Status, Amount,
                  RequestPayload(jsonb), ResponsePayload(jsonb), AttemptedAt
Expense           Id, ProjectId FK, CategoryId FK, Amount, Currency, IncurredAt,
                  Description, VendorName, PaymentMethod,
                  Status(draft|submitted|approved|rejected|paid|reimbursed),
                  SubmittedBy, ApprovedBy, ApprovedAt, ReceiptAssetId FK
ExpenseCategory   Id, Name(bn/en), ParentId FK, IsActive
Budget            Id, FiscalYearId FK, ProjectId FK, CategoryId FK,
                  AllocatedAmount, Notes
FiscalYear        Id, Name, StartDate, EndDate, Status(open|closed)
LedgerEntry       Id, FiscalYearId FK, EntryDate, AccountId FK, DebitAmount,
                  CreditAmount, SourceType(donation|expense|due|adjustment),
                  SourceId, Description, PostedBy, ReversalOfId FK
Account           Id, Code, Name(bn/en), Type(asset|liability|income|
                  expense|equity), ParentId FK
BankAccount       Id, Name, AccountNumberMasked, Provider, OpeningBalance,
                  CurrentBalanceCached, IsActive
Reconciliation    Id, BankAccountId FK, PeriodStart, PeriodEnd, StatementAssetId FK,
                  Status, ReconciledBy, ReconciledAt, Difference
```

**Money rules (non-negotiable):** all amounts stored as `decimal(18,2)` with an explicit ISO-4217 `Currency`; never `float`/`double`. Financial records are **append-only** — corrections are reversing entries, never updates or deletes. Every write to `LedgerEntry` is audited.

### 3.6 Events & volunteering (P5)

```
Event             ContentItemId PK/FK, StartsAt, EndsAt, Timezone, VenueName,
                  VenueAddress, Latitude, Longitude, IsOnline, MeetingUrl,
                  Capacity, RegistrationOpensAt, RegistrationClosesAt,
                  RequiresApproval, Fee, EventType(orientation|training|
                  fundraiser|workshop|meetup|campaign)
EventSession      Id, EventId FK, Title, StartsAt, EndsAt, PresenterName, Order
EventRegistration Id, EventId FK, UserId FK?, Name, Email, Phone,
                  Status(registered|waitlisted|confirmed|cancelled|attended|no_show),
                  RegisteredAt, CheckedInAt, TicketCode, PaymentId FK
VolunteerHour     Id, MemberId FK, ProjectId FK, EventId FK?, Date, Hours,
                  Description, Status(submitted|approved|rejected),
                  ApprovedBy, ApprovedAt
TaskAssignment    Id, Title, Description, ProjectId FK, AssignedToMemberId FK,
                  AssignedBy, DueAt, Priority, Status(todo|in_progress|blocked|done),
                  CompletedAt
Beneficiary       Id, ProjectId FK, InstitutionName, Type(school|madrasa|mission|
                  community), Address, StudentCount, MoUSignedAt, MoUAssetId FK,
                  ContactPerson, Status
```

### 3.7 Communications (P6)

```
NotificationTemplate Id, Key, Channel(email|sms|push|in_app), Language,
                  Subject, Body, Variables(jsonb), IsActive, Version
Notification      Id, RecipientUserId FK?, RecipientEmail, TemplateKey, Channel,
                  Payload(jsonb), Status(queued|sent|delivered|bounced|failed|read),
                  ScheduledFor, SentAt, ProviderMessageId, FailureReason
EmailCampaign     Id, Name, SegmentQuery(jsonb), TemplateId FK, Status(draft|
                  scheduled|sending|sent|cancelled), ScheduledFor, SentAt,
                  RecipientCount, OpenCount, ClickCount
OutboxMessage     Id, Type, Payload(jsonb), OccurredAt, ProcessedAt,
                  Attempts, LastError  — transactional outbox for all side effects
```

### 3.8 Platform

```
FeatureFlag       Id, Key, IsEnabled, Description, RolloutRule(jsonb)
Setting           Id, Key, Value(jsonb), Scope(global|module), IsSecret
IdempotencyKey    Id, Key, Endpoint, RequestHash, ResponseBody, ResponseStatus,
                  CreatedAt, ExpiresAt
```

---

## 4. Engineering standards

These apply from P1 onwards and are enforced in code review and CI.

### 4.1 API conventions

| Concern | Standard |
|---|---|
| Base path | `/api/v{major}/…` — currently `v1`. Breaking changes bump the major and run both for ≥ 90 days |
| Public vs admin | Public reads under `/api/v1/{module}/…`; authenticated management under `/api/v1/admin/{module}/…` |
| Errors | **RFC 9457 Problem Details** (`application/problem+json`) with a stable `type` URI, `title`, `status`, `detail`, `instance`, plus an `errors` object for field validation. Migrate the current `{ error: { code, message } }` shape at the `v1` → `v2` boundary, or add Problem Details alongside it in `v1` with a deprecation notice |
| Paging | Cursor-based (`?cursor=&limit=`) for feeds; offset (`?offset=&limit=`) retained for admin tables. Envelope: `{ items, total?, nextCursor?, limit, offset? }`. `limit` max 100 |
| Language | `?lang=bn\|en` plus `Accept-Language` fallback; **responses must include the resolved language**. Admin endpoints return *all* translations, never one |
| Filtering/sorting | `?status=`, `?tag=`, `?from=`, `?to=`, `?sort=field:asc\|desc` — whitelisted fields only |
| Idempotency | `Idempotency-Key` header honoured on all public POSTs; replays return the original response |
| Concurrency | `ETag` + `If-Match` on admin updates; return `409` on conflict |
| Dates | ISO-8601 with offset (`DateTimeOffset`), UTC in storage. Bengali-numeral display formatting is a **frontend** concern — the API never returns localised numerals |
| IDs | Server-generated ULID/GUIDv7 strings. Slugs are the public-facing identifier for content |
| Caching | `Cache-Control` + `ETag` on public reads; `stale-while-revalidate` at the CDN |
| Health | `/health/live`, `/health/ready`, `/health/startup` per service; aggregated at the gateway |
| Docs | OpenAPI generated from code, `openapi.v1.yaml` produced by CI and committed; Scalar reference in non-production |

### 4.2 ADR queue

Architecture decisions are recorded in `docs/adr/NNNN-title.md`. Ratify these before P1 code starts:

| ADR | Decision | Recommendation / status |
|---|---|---|
| 001 | Modular monolith vs microservices | **Accepted: true microservices** behind the gateway ([ADR-001](adr/0001-true-microservices.md), §2.1) |
| 002 | Identity: ASP.NET Core Identity vs hand-rolled | **ASP.NET Core Identity** — password hashing, lockout, MFA, token providers are solved problems and the legacy system already used it |
| 003 | Token format | Asymmetric **JWT** (RS256/ES256), 15-min access token, rotating refresh token stored hashed |
| 004 | Database | **PostgreSQL 17**, schema per service, Npgsql provider |
| 005 | Background jobs | **Hangfire** (has a dashboard, low ceremony) or Quartz.NET; transactional outbox for side effects |
| 006 | Object storage | **Cloudflare R2** or S3-compatible; no blobs in the database |
| 007 | Email provider | Transactional: Postmark/SendGrid/Amazon SES. Must support bn/en templates and DKIM/SPF on the org domain |
| 008 | Payments | Bangladesh-first: **bKash Merchant + Nagad + SSLCommerz**; Stripe/PayPal for diaspora donors |
| 009 | Validation | **FluentValidation** with a shared endpoint filter |
| 010 | Mapping | Explicit mapping extensions or Mapperly (source-generated); avoid runtime reflection mappers |
| 011 | Observability | **OpenTelemetry** → OTLP; Serilog for structured logs |
| 012 | Testing | xUnit + FluentAssertions + **Testcontainers** (real PostgreSQL) + WebApplicationFactory |
| 013 | Hosting | Container-based: Azure Container Apps / AWS App Runner / Hetzner + Docker Compose / Fly.io. Choose on cost — a non-profit budget favours Hetzner or Fly.io |
| 014 | Data residency & privacy | Where personal data of Bangladeshi applicants is stored; retention windows |

### 4.3 Coding standards

- Nullable reference types on, warnings as errors in CI.
- `dotnet format` enforced; `.editorconfig` committed.
- Every module: `Endpoints/`, `Domain/`, `Data/`, `Services/`, `Contracts/`, `Validators/`.
- No business logic in endpoint delegates beyond orchestration.
- Every public endpoint has an integration test; every domain rule has a unit test.
- No secrets in `appsettings.json` — user-secrets locally, environment/vault in deployment.
- All personal data access goes through a repository that can be audited.

---

## 5. Phase plan

Each phase is independently releasable and paired with the same-numbered frontend phase.

---

### P0 — Foundation hygiene (no frontend dependency)

**Goal.** Make the repository safe to work in and honest about its state, while the frontend ships the static site.

**Scope**

| ID | Deliverable |
|---|---|
| B0-1 | Backend README per [`MASTER_PLAN.md`](MASTER_PLAN.md) §4.4, including the explicit "not production-safe" security notice |
| B0-2 | Reconcile `PROJECT_CONTEXT.md` with this plan; remove duplicated/stale content |
| B0-3 | `.editorconfig`, `Directory.Build.props` (shared `TargetFramework`, nullable, warnings-as-errors, analyzers), `Directory.Packages.props` for central package versions |
| B0-4 | Test projects scaffolded: `tests/UnitTests`, `tests/IntegrationTests` with WebApplicationFactory + Testcontainers, plus smoke tests for every existing endpoint |
| B0-5 | `Dockerfile` per service + `docker-compose.yml` (services + PostgreSQL + Redis + MailHog) so a contributor can run everything with one command |
| B0-6 | GitHub Actions CI: restore → build → `dotnet format --verify-no-changes` → test → publish OpenAPI artefact |
| B0-7 | Populate `BuildingBlocks`: `ProblemDetails` factory, `Result<T>`, paging envelope, `Language` resolution, `ApiError` compatibility shim, endpoint filter base |
| B0-8 | Ratify ADRs 001–014; commit them to `docs/adr/` |
| B0-9 | Move `kj-registration*` snapshots to `legacy/` with a `README` stating they are read-only, and exclude them from the solution build |
| B0-10 | Extract `legacy-html/email.html` (from the frontend repo) into `docs/legacy/email-templates/` as the seed for the P6 member-acceptance template |
| B0-11 | Security issue register: file G1–G3 as tracked issues with a "do not deploy publicly until resolved" label |

**Exit criteria.** `docker compose up` starts every service against PostgreSQL-in-container; CI is green on `develop`; README accurately describes reality; ADRs ratified.

**Frontend pairing.** Frontend P0 (static launch) — no runtime coupling. The only shared artefact is the CTA payload shape, which must not change.

**Estimate.** 2 weeks.

---

### P1 — Production-grade platform core

**Goal.** Turn the scaffold into a deployable, secure API the static frontend can safely start calling. This is the phase that closes every 🔴 gap.

**Scope**

| ID | Deliverable | Closes |
|---|---|---|
| B1-1 | **PostgreSQL migration.** Npgsql provider, schema-per-service, re-baselined migrations, seed data, `dotnet ef` scripts, and a documented SQLite→Postgres path for existing dev data | G7 |
| B1-2 | **ASP.NET Core Identity adoption.** Real password hashing, lockout, email confirmation, password reset, security stamps. One-way migration for the seeded dev user; the plain-text column is dropped, not converted | G1 |
| B1-3 | **JWT authentication.** Asymmetric signing keys, 15-min access tokens with `sub`/`roles`/`permissions` claims, rotating refresh tokens stored **hashed**, revocation list, device/session listing, logout-all | G2 |
| B1-4 | **Authorisation.** Replace `dev-admin-token` with policy-based authorisation over permission claims; seed roles and permissions | G3, G13 (partial) |
| B1-5 | **ContentService persistence.** `ContentItem` + `ContentTranslation` + `Project` + `NewsArticle` tables, EF mappings, migrations, and a seeder that imports the current hard-coded projects/news and the frontend's `siteData.js`/`newsData.js` | G4 |
| B1-6 | **Content API contract fix.** Public reads return `{ items, total, limit, offset, lang }`; add `GET /api/v1/content/bootstrap?lang=` returning projects + news + settings in one call for the SPA's cold start. Document the mapping to the frontend's internal shape | G25 |
| B1-7 | **CORS policy.** Per-environment allow-list from configuration; credentials only for the admin origin | G5 |
| B1-8 | **Rate limiting.** ASP.NET Core rate limiter at the gateway: fixed-window on `/auth/login` (5/min/IP), `/auth/register` and `/cta/submissions` (10/hour/IP), sliding window globally. Return `429` with `Retry-After` | G6 |
| B1-9 | **Validation.** FluentValidation + a shared endpoint filter producing RFC 9457 responses with per-field errors | G23, G24 |
| B1-10 | **Gateway configuration.** Move YARP routes/clusters into `appsettings.{Environment}.json`; add health-check-driven destination availability, request/correlation ID propagation, and response compression | G21 |
| B1-11 | **Observability.** Serilog structured JSON logs with correlation IDs, OpenTelemetry traces + metrics, `/health/live|ready`, and a `/metrics` endpoint. PII must never be logged | G10 |
| B1-12 | **Email delivery.** Provider abstraction + bn/en templates for: contact acknowledgement, registration received, admin notification. Sent via the **outbox + background worker**, never inline | G11, G28 |
| B1-13 | **Background jobs.** Hangfire (or Quartz) wired up with a dashboard behind admin auth; outbox dispatcher; retry with exponential backoff and a dead-letter table | G28 |
| B1-14 | **CTA read APIs.** `GET /api/v1/admin/cta/submissions` with filtering/paging, `GET /{id}`, `PATCH /{id}` (status, assignee, notes), CSV export. Anti-spam: honeypot field, timing check, optional Turnstile/hCaptcha | G15 |
| B1-15 | **Backups & DR.** Automated daily PostgreSQL backups with off-site retention, a **tested** restore procedure, and `docs/RUNBOOK.md` | G29 |
| B1-16 | **Secrets management.** Nothing sensitive in the repository; environment variables in deployment; key rotation procedure documented | — |
| B1-17 | **Contract tests.** A CI job that validates the running API against `openapi.v1.yaml` and publishes a typed client the frontend can consume | G26 |

**ADR-001 cross-service requirements.** P1 also needs authenticated service-to-service calls, a service registry for discovery, and trace propagation across the gateway and services. Each service owns its migrations; cross-service workflows use APIs or events rather than direct table writes.

**New/changed endpoints (illustrative)**

```
POST   /api/v1/auth/register                 (rate-limited, email confirmation)
POST   /api/v1/auth/login                    → JWT + rotating refresh
POST   /api/v1/auth/refresh
POST   /api/v1/auth/logout        POST /api/v1/auth/logout-all
POST   /api/v1/auth/password/forgot           POST /api/v1/auth/password/reset
POST   /api/v1/auth/email/confirm             POST /api/v1/auth/email/resend
GET    /api/v1/auth/me                        GET  /api/v1/auth/sessions
GET    /api/v1/content/bootstrap?lang=
GET    /api/v1/content/projects|news          (persisted, paged, ETag'd)
GET    /api/v1/admin/cta/submissions          PATCH /api/v1/admin/cta/submissions/{id}
GET    /api/v1/admin/cta/submissions/export
```

**Acceptance criteria**

- No plain-text password exists anywhere in code or database.
- Restarting the API does not invalidate a logged-in session.
- A brute-force script against `/auth/login` is throttled and the account locks out.
- Content survives a restart and a redeploy.
- The frontend, from its production origin, can call the API without CORS errors.
- A contact submission produces an acknowledgement email to the sender and a notification to the org inbox, both bilingual.
- A restore from backup into a clean database is demonstrated and documented.
- Integration test coverage ≥ 70 % on the Identity, Content, and Engagement modules.

**Frontend pairing.** Frontend P1 (API client layer, feature flags, contract adapters). The frontend may switch `VITE_ENABLE_API_CONTENT` and `VITE_ENABLE_API_CTA` on once B1-6 and B1-14 are live — with static fallback retained.

**Estimate.** 6–8 weeks.

---

### P2 — Content platform and back-office core

**Goal.** Core members manage the website themselves, without a developer.

**Scope**

| Area | Deliverables |
|---|---|
| Content model | Generalised `ContentItem` covering projects, news, pages, FAQ, team members, testimonials, partners, press coverage; tags/taxonomy; related content; `Redirect` table to preserve legacy URLs |
| Editorial workflow | Draft → in review → scheduled → published → archived; scheduled publishing via background job; revision history with diff and restore; preview tokens for unpublished content |
| Translations | Per-language completeness indicator; "missing translation" report; fallback rules |
| Media library | Upload to object storage, virus scan, automatic WebP/AVIF variants + responsive sizes, EXIF stripping, alt text required in both languages, usage tracking ("where is this image used?"), quota |
| RBAC | Full permission model (§3.1); role management UI APIs; per-module permission keys; `SuperAdmin` cannot be deleted |
| Audit log | Every admin mutation writes `AuditLog` with before/after; queryable API with filtering; immutable |
| Back-office APIs | Dashboard summary endpoint (submissions awaiting response, drafts pending review, recent activity), global admin search, bulk actions |
| Site settings | Navigation menus, contact details, social links, footer content, feature toggles, maintenance mode, homepage section ordering — all editable, all bilingual |
| Engagement | Submission inbox with assignment, canned bilingual replies, spam training, SLA timers |
| Public extras | `GET /sitemap.xml`, `GET /feed.rss` and `/feed.atom` per language, `GET /api/v1/content/search?q=` (PostgreSQL full-text with Bengali support), Open Graph metadata per item |
| Platform | Idempotency middleware (G27), output caching + Redis, ETag/If-Match on admin writes |

**Acceptance criteria**

- A core member with the `ContentEditor` role can create a bilingual news article with an image, schedule it, and see it appear on the public site — with no developer involvement and no deployment.
- Deleting content is soft; an admin can restore it.
- Every mutation appears in the audit log with the actor and a before/after diff.
- Uploading a 6 MB phone photo yields correctly sized, compressed variants automatically.
- Search returns sensible results for Bengali queries.

**Frontend pairing.** Frontend P2 — admin shell (login, layout, guarded routes, role-aware navigation), content editors, media picker, and the switch from build-time JSON to the Content API.

**Estimate.** 8–10 weeks.

---

### P3 — Membership and volunteer lifecycle

**Goal.** Restore and modernise the capability the legacy `kj-registration` system provided, and give membership officers a real pipeline.

**Scope**

| Area | Deliverables |
|---|---|
| Application intake | Public application API accepting the **full legacy field set**; file attachments (CV, photo); duplicate detection by email/phone; recruitment campaigns ("Projonmo — Boshonto 1424 / 2017" style seasons) with open/close windows; application reference number and public status lookup |
| Review pipeline | Statuses `awaiting → under_review → shortlisted → interview_scheduled → approved / rejected / postponed / withdrawn`; bulk transitions; reviewer assignment; scoring rubric; internal notes; interview scheduling with calendar invites; full `ApplicationEvent` history |
| Approval → member | Approving an application provisions a `User` + `Member` + `MemberProfile`, allocates a `MemberCode`, assigns the `Member` role, and sends the welcome/orientation email (seeded from the legacy `email.html` template) |
| Member self-service | Profile view/edit with a moderation queue for sensitive fields, photo upload, privacy/consent settings, password and session management, download-my-data, membership card (PDF/QR) |
| Directory | Internal member directory with search and filters; public directory only for members who consented, showing only fields they approved |
| Structure | Committees/chapters, positions, terms, org-chart API |
| Dues | Optional membership dues: schedules, invoices, reminders, waivers; links to the Finance module |
| Lifecycle | Leave of absence, alumni status, termination with reason, re-activation, tenure calculation |
| Privacy | Retention policy for rejected applications (auto-purge after N months), explicit consent records, export and erasure endpoints (G30) |

**Acceptance criteria**

- A public application submitted through the frontend reaches the pipeline with every legacy field intact.
- A membership officer moves 20 applications through the pipeline in bulk and every transition is audited and notified.
- Approving an application produces a working login, a member record, a member code, and a bilingual welcome email — in one action.
- A member can update their own profile and download all data held about them.
- Rejected application data is purged automatically per the retention setting.

**Frontend pairing.** Frontend P3 — full application form, application status lookup, member portal, membership back-office screens.

**Estimate.** 8–10 weeks.

---

### P4 — Donations and financial management

**Goal.** Accept money online, account for it correctly, and give the finance officers a real back office. **This is the highest-risk phase — treat correctness and auditability as non-negotiable.**

**Scope**

| Area | Deliverables |
|---|---|
| Donation intake | One-time and recurring donations; amount presets and custom amounts; multi-currency (BDT primary, USD/GBP/EUR for diaspora); designate to a campaign or project; anonymous option; dedication messages; "cover the transaction fee" option |
| Payment gateways | Provider abstraction with adapters — bKash Merchant, Nagad, SSLCommerz (covers cards + local wallets), Stripe and PayPal for international. Hosted-checkout redirect flow; **webhook handlers with signature verification**, replay protection, and idempotent processing; reconciliation of gateway payouts against recorded donations |
| Offline donations | Manual recording of bank transfers, cash, cheques and in-kind gifts, with attachment of proof and a maker-checker approval step |
| Receipts | Sequential, tamper-evident receipt numbers; bilingual PDF generation; automatic email delivery; re-issue with audit trail; annual donor statements |
| Donor CRM | Donor records with giving history, lifetime value, segmentation, communication consent, thank-you automation, lapsed-donor detection, major-donor flags |
| Campaigns | Fundraising campaigns with goals, live progress totals (cached), deadlines, and public progress API |
| Expenses | Submission with receipt upload, category and project coding, configurable multi-step approval, reimbursement tracking, per-project spend rollups |
| Budgets | Fiscal years, per-project and per-category budgets, budget-vs-actual variance, over-budget alerts |
| Ledger | Double-entry `LedgerEntry` + chart of `Account`s; automatic posting from donations, expenses and dues; **append-only with reversing entries**; period close/lock |
| Banking | Bank account registry, statement import (CSV/OFX), assisted reconciliation, unmatched-transaction queue |
| Reports | Income statement, balance sheet, cash-flow, donation summary by channel/campaign/period, project cost report, donor reports, statutory export formats for the auditor |
| Controls | Segregation of duties (the submitter of an expense cannot approve it), approval thresholds by amount, mandatory audit log, four-eyes principle on ledger adjustments, refund workflow |

**Acceptance criteria**

- A donation from bKash sandbox flows end-to-end: initiate → redirect → webhook → `succeeded` → ledger posting → receipt PDF → donor email.
- Replaying a webhook ten times produces exactly one donation and one ledger posting.
- A gateway timeout leaves the donation in `pending`, never in a wrong terminal state, and a reconciliation job resolves it.
- Ledger debits equal credits for every posting; a trial balance report proves it.
- No user can both submit and approve the same expense.
- A finance officer produces a month's financial report without SQL access.
- All monetary calculations are exact decimal arithmetic — verified by property-based tests.

**Frontend pairing.** Frontend P4 — donation flow, campaign pages with progress, donor receipt portal, finance back-office screens.

**Estimate.** 10–12 weeks. Add lead time for merchant-account approvals, which are often the critical path.

---

### P5 — Events, programmes and volunteer operations

**Goal.** Run the organisation's actual programme work through the platform.

**Scope**

| Area | Deliverables |
|---|---|
| Events | Full event CRUD with bilingual content, venue/online details, capacity, sessions/agenda (the legacy orientation email's schedule table is the model), recurring events, event series |
| Registration | Public RSVP with or without an account, capacity and waitlist handling, approval-required events, paid events via the Finance module, QR ticket codes, cancellation |
| Attendance | Check-in (QR scan or manual), no-show tracking, attendance certificates, post-event feedback forms |
| Volunteer ops | Volunteer hour logging with approval, per-project and per-member hour rollups, volunteer opportunity board with skill matching, shift scheduling |
| Programme delivery | Beneficiary institutions (schools/madrasas/missions), MoU records with document attachments, per-institution student counts, session/class logging against the Pathshala model, outcome metrics per project |
| Tasks | Lightweight task assignment for committees and projects, due dates, status, per-member workload view |
| Calendar | iCal feed per member and per public event; calendar invites on registration |

**Acceptance criteria**

- An event is created, published bilingually, receives 100 registrations with a waitlist at capacity, and check-in works from a phone.
- Volunteer hours submitted by a member are approved by a coordinator and appear in that member's profile and the project rollup.
- A project shows its beneficiary institutions, MoU documents, sessions delivered and students reached.

**Frontend pairing.** Frontend P5 — events listing/detail/RSVP, volunteer portal, event back office.

**Estimate.** 6–8 weeks.

---

### P6 — Communications, community and press

**Goal.** Let the organisation reach its audience and show its public standing.

**Scope**

| Area | Deliverables |
|---|---|
| Newsletter | Double opt-in subscription, preference centre, one-click unsubscribe (RFC 8058), segmentation (members / donors / subscribers / event attendees), bounce and complaint handling |
| Campaign email | Bilingual template library seeded from the legacy `email.html`, visual/Markdown composer, test sends, scheduled sends, throttled delivery, open/click tracking with a privacy-respecting default, delivery reports |
| Transactional notifications | Complete template catalogue: application received / status changed / approved, donation receipt, event registration and reminder, dues reminder, password reset, admin alerts — all bn/en, all versioned |
| Channels | Email (primary), SMS for Bangladesh numbers (OTP, event reminders) via a local provider, in-app notifications, optional web push |
| Press & media | `press_coverage` content type: newspaper and TV mentions with outlet name, logo, date, external URL, clipping image/PDF, excerpt, language; featured-coverage ordering for the homepage press strip |
| Community | Testimonials with approval workflow and consent; partner/sponsor registry with logo, tier and link; success stories tied to projects and beneficiaries |
| Social | Auto-generated Open Graph images per content item; share-count tracking; optional scheduled cross-posting |
| Inbox | Unified engagement inbox (contact, partnership, volunteering, media enquiries) with routing rules and SLA reporting |

**Acceptance criteria**

- A newsletter goes to 1,000 subscribers with correct bn/en per subscriber preference, working unsubscribe, and a delivery report.
- Bounces and spam complaints automatically suppress future sends.
- A press mention added by an editor appears in the homepage press strip and on `/press` with its clipping.
- Every transactional email has both language versions and renders correctly in Gmail, Outlook and a mobile client.

**Frontend pairing.** Frontend P6 — press/media page, newsletter signup and preference centre, testimonials and partners sections.

**Estimate.** 6–8 weeks.

---

### P7 — Reporting, analytics and transparency

**Goal.** Turn accumulated data into decisions internally and trust externally.

**Scope**

| Area | Deliverables |
|---|---|
| Back-office dashboards | Role-aware home dashboards: membership funnel, donation trend vs target, expense burn vs budget, event attendance, content pipeline, engagement inbox SLA |
| Impact metrics | Configurable metric definitions (students reached, institutions engaged, volunteer hours, sessions delivered), per-project and organisation-wide, with time series |
| Reporting engine | Saved report definitions, parameterised runs, scheduled delivery by email, exports to CSV/XLSX/PDF |
| Public transparency | Public API for headline figures (funds raised, funds spent by category, beneficiaries reached, volunteer count) with an explicit "what we publish" policy; annual report publishing with downloadable PDFs; audited-accounts archive |
| Analytics | Server-side event collection for the frontend's analytics hooks, privacy-preserving (no third-party cookies, IP hashing), funnel analysis for donation and application journeys |
| Data quality | Anomaly detection on donations, duplicate-record detection, completeness reports |
| Warehouse-lite | Nightly aggregate tables so dashboards never query the transactional tables directly |

**Acceptance criteria**

- The executive dashboard loads in under two seconds against three years of data.
- Public transparency figures reconcile exactly with the ledger.
- A trustee receives the monthly financial and impact report by email without anyone running it manually.

**Frontend pairing.** Frontend P7 — public impact/transparency page, annual reports archive, back-office dashboards.

**Estimate.** 5–6 weeks.

---

### P8 — Scale, hardening and platform maturity

**Goal.** Make the platform durable beyond the current volunteer team.

**Scope**

- External **security audit and penetration test**; remediation to closure; dependency scanning (Dependabot, `dotnet list package --vulnerable`) and SBOM generation in CI.
- Performance: query tuning, index review, N+1 elimination, response caching, CDN in front of the API for public reads, load testing to an agreed target.
- Resilience: graceful degradation, circuit breakers on gateway calls, retry policies, chaos testing of the payment webhook path.
- Multi-region backups, documented **and rehearsed** disaster recovery with stated RPO/RTO.
- Public **developer API**: API keys, scopes, quotas, documentation portal — for partner integrations.
- Webhooks outbound so partners can subscribe to organisation events.
- Service isolation review: move a service to separate hosting or a domain when its scaling, security, or operational needs justify it; retain its API and data ownership boundaries.
- Mobile/API readiness: refresh-token flows for native clients, push notification registration.
- i18n expansion beyond bn/en if needed; translation-management workflow.
- Operational maturity: on-call rota, runbooks per incident class, SLOs and error budgets, quarterly restore drills.

**Estimate.** Continuous from P4 onwards; ~6 weeks of dedicated effort.

---

## 6. Non-functional requirements

| Requirement | Target |
|---|---|
| Availability | 99.5 % monthly for public read APIs |
| Latency | p95 < 300 ms for public reads (cached), < 800 ms for admin queries, < 2 s for report generation |
| Throughput | 100 req/s sustained on public reads; the site must survive a press-mention traffic spike |
| Data durability | RPO ≤ 24 h (P1), ≤ 1 h (P4 onwards for financial data); RTO ≤ 4 h |
| Payload limits | 32 KB JSON default; 10 MB uploads; 25 MB for statements/annual reports |
| Localisation | Every user-visible string bn + en; no hard-coded English in responses |
| Accessibility | Admin UI targets WCAG 2.1 AA (API must supply the data the UI needs to meet it) |
| Test coverage | ≥ 70 % line coverage overall; ≥ 90 % on Finance domain logic |
| Build | CI < 10 minutes; deployment < 5 minutes; zero-downtime rolling deploys from P1 |

---

## 7. Security and compliance

| Control | Requirement | Phase |
|---|---|---|
| Password storage | ASP.NET Identity hasher (PBKDF2) or Argon2id; never reversible | P1 |
| Token security | Asymmetric JWT, short-lived access tokens, hashed rotating refresh tokens, revocation on password change | P1 |
| Transport | TLS 1.2+ everywhere, HSTS, no plain-HTTP listeners in production | P1 |
| Input handling | Validate and whitelist everything; parameterised queries only; strict output encoding; HTML sanitisation on rich content | P1–P2 |
| Rate limiting & bot defence | Per-IP and per-account limits, honeypot fields, CAPTCHA on abused endpoints | P1 |
| Secrets | Environment/vault only; rotation procedure; no secrets in git history (scan and remediate) | P1 |
| Authorisation | Deny by default; permission checks at the endpoint; object-level ownership checks on member self-service | P1–P3 |
| Audit | Immutable audit log on all admin and financial mutations | P2, P4 |
| PII | Field-level encryption for sensitive profile data; PII never in logs; data-subject export and erasure endpoints; documented retention windows | P3 |
| Financial controls | Segregation of duties, approval thresholds, append-only ledger, four-eyes on adjustments, webhook signature verification | P4 |
| Uploads | Type/size validation, virus scanning, EXIF stripping, served from a separate origin, no execution | P2 |
| Headers | CSP, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, CORS allow-list | P1 |
| Dependencies | Automated vulnerability scanning; patch SLA of 7 days for critical | P0 onwards |
| Incident response | Documented plan, breach-notification procedure, contact tree | P4 |

**Do not deploy the current `develop` state to a public address.** G1–G3 make it trivially compromisable.

---

## 8. Environments, deployment and operations

| Environment | Purpose | Data | Deploy trigger |
|---|---|---|---|
| Local | Development | Seeded fixtures via `docker compose` | Manual |
| CI | Automated tests | Ephemeral Testcontainers | Every PR |
| Staging | Integration + UAT + gateway sandbox testing | Anonymised copy | Merge to `develop` |
| Production | Live | Real | Tagged release from `main`, manual approval |

**Deployment.** Containers built in CI, tagged by commit SHA, pushed to a registry, deployed by digest. Migrations run as a pre-deploy step with a rollback plan; all migrations must be backward-compatible for one release (expand → migrate → contract). Health checks gate traffic. Blue/green or rolling with automatic rollback on failed health checks.

**Branching.** `main` = production, `develop` = integration, `feature/*` and `fix/*` off `develop`, squash merges, conventional commits, release tags `v{major}.{minor}.{patch}`.

**Operations.** Structured logs shipped to a queryable store, alerting on error rate / latency / failed payments / queue depth / backup failure, a status page, and a runbook per incident class.

---

## 9. Frontend alignment matrix

The contract each phase must deliver **before** the paired frontend work can leave its static fallback.

| Phase | Backend delivers | Frontend consumes | Coupling |
|---|---|---|---|
| P0 | Nothing runtime-facing; CTA payload shape frozen | Static forms using the frozen shape | None — fully parallel |
| P1 | Auth, persisted content, `/content/bootstrap`, CTA read/write, CORS, email | API client + `VITE_ENABLE_API_CONTENT` / `VITE_ENABLE_API_CTA` flags | Loose — flags default off, static fallback retained |
| P2 | Content CRUD, media, RBAC, settings, search, audit | Admin shell + content editors + dynamic navigation/settings | Tight — build against staging behind a flag |
| P3 | Application intake, pipeline, member self-service | Application form, status lookup, member portal, membership back office | Tight |
| P4 | Donations, gateways, receipts, expenses, ledger, reports | Donation flow, campaign pages, receipts, finance back office | Tight — sandbox credentials needed in staging early |
| P5 | Events, registration, attendance, volunteer hours | Event pages, RSVP, volunteer portal | Tight |
| P6 | Newsletter, campaigns, notifications, press coverage | Press page, newsletter UI, preference centre | Medium — press coverage can start as static content |
| P7 | Reporting, transparency, analytics ingest | Dashboards, impact page, annual reports | Medium |
| P8 | Hardening, developer API, webhooks | PWA, performance work | Low |

**Rules of engagement**

1. Every phase begins by agreeing the OpenAPI contract; the frontend codegens a typed client from it and can build against a mock server immediately.
2. Backend deploys to staging before the paired frontend feature merges.
3. No frontend feature ships without a working static fallback or an off-by-default flag.
4. Contract changes within a phase require a version bump or an additive-only change; breaking changes wait for the next major.

---

## 10. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Current auth is deployed publicly before P1 completes | Critical — full compromise | Deployment blocked by policy until G1–G3 close; issues labelled and tracked |
| Payment gateway approval (bKash/Nagad merchant accounts) takes months | Delays P4 | Start applications during P2; build against sandbox; ship offline-donation recording first |
| Financial correctness bugs | Severe reputational and legal damage to a non-profit | Double-entry ledger, append-only records, 90 % test coverage on Finance, external review before go-live |
| Microservice overhead exhausts a volunteer team | Slow delivery, half-finished services | Keep service boundaries narrow, automate deployment and observability, and track per-service operating cost; revisit ADR-001 if the overhead becomes unsustainable |
| Volunteer developer turnover | Knowledge loss | This document, ADRs, README, runbooks; no undocumented tribal knowledge |
| Personal data of applicants mishandled | Legal and trust damage | Retention policy, encryption, access audit, minimal collection |
| Hosting cost exceeds a non-profit budget | Service interruption | Cost-conscious ADR-013; co-host services where capacity allows, measure cost as services grow, and apply for non-profit cloud credits |
| Contract drift between frontend and backend | Rework, integration failures | OpenAPI as the contract, CI contract tests, generated clients |
| Scope creep across nine phases | Nothing finishes | This plan is the source of truth; changes go through it first |

---

## 11. Open decisions

Owned by the core members; each blocks the phase noted.

1. **Hosting and budget** — monthly ceiling and preferred provider? *(blocks P1 deployment)*
2. **Payment providers** — which merchant accounts can realistically be obtained, and who owns the applications? *(blocks P4)*
3. **Fiscal calendar** — Bangladesh fiscal year (July–June) or calendar year? Which accounting standard does the auditor expect? *(blocks P4 ledger design)*
4. **Membership model** — are there paid dues, and what member types exist? *(blocks P3)*
5. **Data retention** — how long are rejected applications and donor records kept? *(blocks P3, P4)*
6. **Public transparency** — which financial figures will the organisation publish? *(blocks P7)*
7. **Back-office users** — how many, in which roles? *(blocks P2 RBAC seeding)*
8. **Email domain** — who controls DNS for `kolpojontro.org` to configure SPF/DKIM/DMARC? *(blocks P1 email)*
9. **Legal entity details** — registration number, trustees, audit obligations. *(blocks P7 and receipt content)*
