<!-- phase: PLAN | branch: feature/issue-31-observability | tasks: 0/5
     base: — | updated: 2026-10-30
     next: await plan approval, then branch feature/issue-31-observability (feature/issue-30-gateway-config) -->
# Plan: Observability: Serilog, health, metrics
## Tasks
- [ ] BuildingBlocks: AddKjLogging (Serilog JSON, correlation enricher, redaction), AddKjHealth
- [ ] Wire into 4 services; /health/live|ready|startup; gateway aggregate
- [ ] /metrics endpoint (restricted to non-public)
- [ ] Tests: JSON shape + correlation ID, PII redaction, health states
- [ ] Docs, full suite + format
## Tests
- High blast radius for PII: redaction tested with names/phones/emails/tokens
## Files
- src/BuildingBlocks, all service Program.cs, tests, docs
