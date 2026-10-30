<!-- phase: DONE | branch: feature/issue-30-gateway-config | tasks: 5/5
     base: 891a4b8 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: Gateway routing from configuration
## Tasks
- [x] Move routes/clusters to config (Development + Production sample), remove hard-coded Program.cs
- [x] Active health checks + passive; unhealthy destination drops out
- [x] Correlation ID middleware (create/echo/forward)
- [x] Response compression (br/gzip) on public reads
- [x] Tests (routing from config, unhealthy dest, header propagation, compression), docs, full suite
## Tests
- Standard: gateway integration tests with stub destinations
## Files
- src/Services/ApiGateway/**, tests, docs
