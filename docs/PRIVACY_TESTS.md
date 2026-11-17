# Privacy test suite (BE-109, #216)

## Run locally

From the repository root:

```sh
dotnet test tests/IntegrationTests/KJWebsite.IntegrationTests --filter "FullyQualifiedName~Privacy"
```

No Docker or external services are needed. The gateway and services run in memory through ASP.NET Core TestServer; persistence uses temporary SQLite files, removed at disposal.
The filter also runs `PrivacyTests`, which covers membership retention, consent, export and erasure.

## What the suite proves

The sponsorship suite uses 18 scenario ids from T-01 to T-29; the numbering is not continuous. Each entry names its test class.

### Access and ownership

- **T-01** (`PrivacyEndpointMatrixTests`): every protected endpoint rejects missing, malformed and expired tokens with 401; Identity also rejects synthetic sessions.
- **T-02** (`PrivacyEndpointMatrixTests`): donor tokens cannot call staff endpoints, and refused writes, sent with valid bodies and real targets, leave the full row content of every service database unchanged.
- **T-07** (`PrivacyEndpointMatrixTests`): callers without the required permission or role receive 403; session-only Identity routes have explicit expectations.
- **T-03** (`PrivacyEndToEndTests`): another donor's student detail and photo match an unknown student's 404; query and body ids cannot change ownership.
- **T-04** (`PrivacyEndToEndTests`): donor photos require an owned link and current consent; staff photos require the staff permission.
- **T-05** (`PrivacyEndToEndTests`): donor self-service paths reject writes without changing responses or business data.

### Frozen copies, mistaken links and signed forms

- **T-06** (`PrivacyEndToEndTests`): ending sponsorship freezes the shared fields, and later staff edits do not change the donor's copy.
- **T-11** (`PrivacyEndToEndTests`): a mistaken link disappears from donor reads without a frozen copy while staff history remains.
- **T-24** (`PrivacyEndToEndTests`): only SuperAdmin can void a mistaken link; every other seeded role is refused without changing data.
- **T-25** (`PrivacyEndToEndTests`): signed consent forms require staff permission, with 401 or 403 for unauthorised callers.
- **T-26** (`PrivacyEndToEndTests`): donors cannot retrieve signed forms through mapped staff paths or guessed donor paths.

### Account state, passwords and concurrency

- **T-14** (`PrivacyEndToEndTests`, `PrivacySessionEndToEndTests`): the original Finance-disable check remains; with the session check enabled, disabling through Finance or directly in Identity makes the old token return 401 on every donor route, rejects refresh and makes sign-in return `403 ACCOUNT_DISABLED`.
- **T-27** (`PrivacyEndToEndTests`): wrong-password and unknown-email failures have identical bodies after request-specific ids are removed.
- **T-28** (`PrivacyEndToEndTests`): provision, reset and password change enforce the initial-change flag and expose no password in responses, audits or logs.
- **T-29** (`PrivacyEndToEndTests`): student and donor PATCH without a current ETag returns 428 or 412 and preserves fields and versions.

### Public responses, caching and logs

- **T-21** (`PrivacyPublicRouteTests`): every public OpenAPI operation, called once with placeholder ids and once with the seeded real ids where the parameter fits, keeps seeded private values (including the child's date of birth and the guardian phone) out of bodies and headers; search probes disclose no private results.
- **T-22** (`PrivacyEndToEndTests`): private reads carry no-store headers, have no Age header and reach the service on both requests.
- **T-23** (`PrivacyLogScanTests`): the shared log sink contains no registered private values from any factory.

Related tests remain in `SponsorshipStudentTests` (projection and consent), `SponsorshipLinkTests` (frozen photos, consent withdrawal and paging), `SponsorshipDesignationTests` (designation dates) and `DonorSelfServiceTests` (reversals and donation paging).
Those classes are outside the Privacy filter; run the integration project without the filter to include them.

## How the harness works

`PrivacyStack` starts the gateway plus Identity, Content, CTA, Membership, Finance and Sponsorship once per test class.
YARP forwards to TestServer handlers. Membership and Finance call the real test Identity; Finance and Sponsorship call each other through handlers. Sponsorship's project catalogue uses `FakeProjects`.
Staff accounts use real Identity sessions. Helpers provision donors, change initial passwords, upload forms and photos, grant consent and create links through the gateway.

The test factories and `PrivacyStack` turn the session check off by default. `PrivacySessionStack` enables it for Finance, Sponsorship, Membership, Content and CTA and routes their checks to the real test Identity through TestServer handlers. `PrivacySessionEndToEndTests` covers staff promotion and demotion, role permission edits, Identity and Finance donor disable, staff password reset, member-to-donor role grant, logout and logout-all. It checks old access and refresh tokens, multiple devices and fresh sign-in with updated claims. It also simulates an Identity outage to check `503 AUTH_UNAVAILABLE`, unaffected public pages and recovery with the same tokens, and watches issued access and refresh tokens for log leaks.

`Seed(label, value)` registers a private value with `GlobalLogSink.Default`; `NewSeed(prefix)` registers a unique `KJPRIV-...` value. Register before exercising the request; earlier log lines are not scanned again. Seeds must have at least 12 characters.
The process-wide sink checks each log write, case-insensitively, including category, level, formatted message, exception and scopes. All service and gateway factories attach it, so a seeded value in logs fails a test when `AssertNone()` runs, including at factory disposal.
`PrivacyLogScanTests` supplies a non-parallel final guard, and `PrivacyHarnessLogSinkTests` checks the detector itself.
The counting handler records gateway forwards per service. T-22 requires the count to rise by two, proving that a repeated request reached the service as well as checking the response headers.

## Covering a new endpoint

`PrivacyEndpointMatrixTests` enumerates `EndpointDataSource` in all six services rather than keeping a route list. A new protected endpoint receives the 401 checks automatically; permission and donor-role policies also receive the applicable 403 checks without editing the matrix.
Every non-infrastructure route must explicitly declare authorisation or anonymous access. Health, OpenAPI, Swagger and Scalar routes are excluded from the protected matrix.
The anonymous set itself is pinned: `TheAnonymousSurfaceIsExactlyTheReviewedList` compares it with `ReviewedAnonymous`, so a protected endpoint that becomes anonymous (or a new anonymous one) fails until a reviewer adds it to that list on purpose.

T-21 reads `openapi.v1.yaml` from the test output. A new public operation is scanned once `openapi.v1.yaml` is regenerated (`dotnet run --project tools/OpenApiExporter`), including writes with an empty JSON body. If a new anonymous endpoint is added to `ReviewedAnonymous` but the YAML is not regenerated, `EveryGatewayAnonymousOperationAppearsInOpenApi` fails, so the scan cannot silently skip it.
Public means outside `/api/v1/admin/`, without a `me` path segment, and outside auth sessions, logout-all and change-password routes. This is a path rule, not OpenAPI security metadata.
If the scan reports `METHOD /path: extend the parameter table for 'name'.`, add a safe value to `PathValues` in `PrivacyPublicRouteTests.cs` (or `QueryValues` for a query parameter). Use a valid placeholder that does not contain a registered private value; do not skip the operation.

## Reading failures

Response leak assertions use `METHOD /path leaked '<value>' (<label>)`. The method and path identify the operation, the value identifies the seeded data, and the label explains what it represents.
Log failures instead identify the logger: `logger '<category>' logged '<value>' (<label>)`. Check the response projection or logging call named by the failure.

## Limits

The suite does not prove Postgres-only behaviour, a deployed gateway's configuration, or a real browser's behaviour.
The **#229 / BE-114** window is closed: every service except Identity re-checks bearer-token sessions with Identity on every request, without a cache. An old token receives `401 UNAUTHORIZED` after revocation, even when Finance's local account state stays active. If Identity is unreachable, times out or returns any status other than 204, 401 or 403, authenticated calls fail closed with `503 AUTH_UNAVAILABLE`; public pages are unaffected. An anonymous endpoint called with a dead token answers as anonymous.
See [DONORS.md](DONORS.md) and [SPONSORSHIP.md](SPONSORSHIP.md) for the endpoint contracts and remaining policy decisions.
