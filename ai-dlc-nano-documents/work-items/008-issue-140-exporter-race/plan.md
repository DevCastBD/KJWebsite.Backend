<!-- phase: DONE | branch: 140-fix-openapi-exporter-concurrent-build | tasks: 3/3
     base: 924f524 | updated: 2026-09-28
     next: — (PR open; then the #141 merge sequence) -->
# Plan: serialise the exporter's builds
## Tasks
- [x] BuildServicesAsync: build each service project once, one after another, before any starts
- [x] Start services with `dotnet run --no-build`
- [x] Verify: race reproduced on old code (1/3 cold runs failed); fixed code 5/5 cold runs pass (7–9s); a real compile error fails clearly naming the service
## Tests
- No unit test: the defect is process-level concurrency; verified by repeated cold runs and a forced compile error (see above)
## Files
- tools/OpenApiExporter/Program.cs
