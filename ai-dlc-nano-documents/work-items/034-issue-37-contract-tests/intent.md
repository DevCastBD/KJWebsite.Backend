# Contract tests against openapi.v1.yaml
- Source: issue #37 https://github.com/DevCastBD/KJWebsite.Backend/issues/37
- Docs: docs/MASTER_PLAN.md, tools/OpenApiExporter
- Type: feature
## Request
- Local dotnet-test category that runs services and fails when runtime API diverges from openapi.v1.yaml, naming endpoint and field.
## Decisions
- No CI job / published client (plan v2)
## Out of scope
- Typed client publishing
## Follow-ups
- none yet
