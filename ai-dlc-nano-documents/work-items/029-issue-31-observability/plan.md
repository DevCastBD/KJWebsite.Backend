<!-- phase: DONE | branch: feature/issue-31-observability | tasks: 5/5
     base: 9d63380 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: Observability: Serilog, health, metrics
## Tasks
- [x] BuildingBlocks: AddKjLogging (Serilog JSON, correlation enricher, redaction), AddKjHealth
- [x] Wire into 4 services; /health/live|ready|startup; gateway aggregate
- [x] /metrics endpoint (restricted to non-public)
- [x] Tests: JSON shape + correlation ID, PII redaction, health states
- [x] Docs, full suite + format
## Tests
- High blast radius for PII: redaction tested with names/phones/emails/tokens
## Files
- src/BuildingBlocks, all service Program.cs, tests, docs
