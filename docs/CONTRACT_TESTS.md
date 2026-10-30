# Contract tests

`openapi.v1.yaml` is the contract the frontend generates its types from (`bun run api:sync`). It is a **build output**: generated from the services' own OpenAPI documents by `tools/OpenApiExporter`, then checked in. Two checks keep it true:

| Check | Command | Fails when |
|---|---|---|
| Spec is current | `dotnet run --project tools/OpenApiExporter -- --verify` | the committed file differs from what the code now generates |
| Running API matches the spec | `dotnet test --filter Category=Contract` | a live response disagrees with the file |

The second one starts the real services in-process (Sqlite, in-memory jobs), calls every documented operation with a probe in `tests/.../Contract/ContractFixture.cs`, and checks the live answer against the committed file:

- the **status** returned is documented for that operation;
- a documented **JSON body** matches its schema: required fields present, types, enums, date-times (extra fields are allowed, since additive change is the rule);
- **every operation in the spec has a probe**, and **every endpoint that runs is in the spec**;
- every operation's 2xx response has a schema (no bare "200 OK").

Output names the endpoint, the status and the field:

```
POST /api/v1/cta/submissions → 201: $.createdOn: required field is missing
GET /api/v1/admin/cta/submissions → 200: $.items[3].submittedAt: 'yesterday' is not an ISO date-time
DELETE /api/v1/auth/sessions/{id} → 404: status is not documented (spec lists 204, 401, 403)
```

## When it fails

- You changed the API on purpose: `dotnet run --project tools/OpenApiExporter -- --output openapi.v1.yaml`, commit the spec, re-run. Breaking changes follow the rules of engagement (additive within a phase; breaking changes wait for the next major version and run in parallel for at least 90 days).
- You added an endpoint: add a `.Produces<T>(…)` for each success status, then a probe in `ContractFixture`. Standard 400/401/403/404 Problem Details responses are added automatically (`KjOpenApiResponses`).
- You did not mean to change the contract: fix the code.

Run the whole suite before a release: `dotnet test KJWebsite.Backend.slnx` includes the contract tests. There is no CI job and no published client (plan v2); the frontend takes the spec with `bun run api:sync`.
