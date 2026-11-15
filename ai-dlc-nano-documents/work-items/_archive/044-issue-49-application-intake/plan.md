<!-- phase: DONE | branch: Feature/issue-49-public-application-intake | tasks: 6/6
     base: ae67e94 | updated: 2026-10-31
     next: none (done) -->
# Plan: Application intake
## Tasks
- [x] MembershipService project (schema membership, DbContext, migration, health, OpenAPI, slnx, gateway route, exporter entry)
- [x] Application entity: legacy fields + email/phone HMAC hashes; sensitive fields encrypted (DataProtection); reference KJ-yyyy-nnnn
- [x] POST applications (validation, idempotency, 201 reference) + POST check (warn-only, rate-limited)
- [x] PUT attachments: magic-byte type check, size caps, safe storage behind IAttachmentStore (local disk); scan/EXIF as a seam until #41
- [x] Logging: no PII (tests assert); permission-gated reads deferred to #51
- [x] Tests, docs/MEMBERSHIP_INTAKE.md, openapi regen, build+format+full suite
## Tests
- Standard + PII: validation, duplicates, idempotent replay, bad magic bytes/oversize, log capture holds no PII
## Files
- src/Services/MembershipService/**, ApiGateway config, tools/OpenApiExporter, tests, docs, openapi.v1.yaml
