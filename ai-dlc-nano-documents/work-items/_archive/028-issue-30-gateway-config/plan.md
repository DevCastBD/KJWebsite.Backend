<!-- phase: PLAN | branch: feature/issue-30-gateway-config | tasks: 0/5
     base: — | updated: 2026-10-30
     next: await plan approval, then branch feature/issue-30-gateway-config (feature/issue-29-validation) -->
# Plan: Gateway routing from configuration
## Tasks
- [ ] Move routes/clusters to config (Development + Production sample), remove hard-coded Program.cs
- [ ] Active health checks + passive; unhealthy destination drops out
- [ ] Correlation ID middleware (create/echo/forward)
- [ ] Response compression (br/gzip) on public reads
- [ ] Tests (routing from config, unhealthy dest, header propagation, compression), docs, full suite
## Tests
- Standard: gateway integration tests with stub destinations
## Files
- src/Services/ApiGateway/**, tests, docs
