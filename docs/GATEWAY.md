# Gateway configuration

Routes, clusters and destinations are configuration (`ReverseProxy` section, YARP), not code.

- `appsettings.json`: routes and per-cluster health checks, identical in every environment.
- `appsettings.Development.json`: `localhost:7001–7003` destinations.
- `appsettings.Production.json`: co-hosted defaults (`127.0.0.1:5001–5003`). Override per host with environment variables, e.g. `ReverseProxy__Clusters__content__Destinations__content__Address=http://10.0.0.5:5001/`.
- Outside Development the gateway refuses to start if a cluster has no destination.

Add a second destination to a cluster (`…Destinations__second__Address`) and the gateway load-balances between them.

## Health-driven availability

Every cluster is probed actively (`GET /health/ready` every 10 s, 5 s timeout, `ConsecutiveFailures`) and passively (transport failures). A destination that fails stays out of rotation until a probe succeeds again, so a rolling deploy does not fail requests.

## Correlation ID

`X-Correlation-Id` is accepted from the caller when it matches `[A-Za-z0-9._-]{8,64}`; otherwise the gateway creates one. It is forwarded to every service, echoed on the response, and added to the logging scope as `CorrelationId` (services pick it up through `UseCorrelationId()` in BuildingBlocks).

## Compression

Brotli and gzip for `GET /api/v1/content/**` (the public reads). Admin, auth and CTA responses are not compressed.
