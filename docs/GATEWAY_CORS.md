# Gateway CORS policy

The gateway chooses one of two CORS policies for each request using its origin.
An origin in `Cors:AdminOrigins` gets the admin policy, with credentials allowed.
An origin in `Cors:SiteOrigins` gets the site policy, without credentials.
Admin origins take priority if an origin appears in both lists.
Any other origin gets no CORS headers.

Configure exact `scheme://host[:port]` values using HTTP or HTTPS, with no wildcards.
An empty origin list rejects everything for that policy.
A wildcard or malformed origin fails startup.
Both lists are empty in `src/Services/ApiGateway/appsettings.json` by default.

Allowed methods: GET, POST, PUT, PATCH, DELETE and OPTIONS.
Preflight responses are cached for one hour.

Allowed request headers:

- `Content-Type`: identifies the request body format.
- `Authorization`: carries the caller's authentication token.
- `Idempotency-Key`: supports safe retries of POSTs; see [IDEMPOTENCY.md](IDEMPOTENCY.md).
- `X-Filename`: supplies the attachment upload name.
- `X-Correlation-ID`: links calls for tracing; the frontend sends it on every call.
- `If-Match`: supports optimistic concurrency. Sponsorship student and finance donor PATCH edits return 428 without it and 412 when it is stale. Content and settings edits (PUT, and the workflow and restore POSTs) accept it optionally: a missing header means last write wins, and a stale value returns 409.

The only exposed response header is `ETag`.
A browser script can read it from any response that carries it.
Without exposure, the client has no value to send back as `If-Match`.

If-Match and ETag were missing until BE-116 (#237), so cross-origin edits were blocked by the browser.

To add another header, update the allow list and the exposed list as appropriate in
`src/Services/ApiGateway/CorsConfiguration.cs`, then add a test in
`tests/IntegrationTests/KJWebsite.IntegrationTests/GatewayCorsTests.cs`.
