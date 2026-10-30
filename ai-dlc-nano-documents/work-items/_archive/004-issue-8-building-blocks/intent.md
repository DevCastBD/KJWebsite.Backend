# Populate BuildingBlocks
- Source: issue #8 https://github.com/DevCastBD/KJWebsite.Backend/issues/8
- Type: feature
## Request
- Replace the placeholder BuildingBlocks project with shared Problem Details, Result, paging, language resolution, compatibility error, and endpoint-filter primitives.
- Adopt shared errors and language normalization in every service.
- Preserve the legacy `{ error: { code, message } }` response shape during the P1 transition.
- Add unit coverage for language resolution and the paging envelope.
## Decisions
- Errors use RFC 9457 Problem Details with the legacy `error` object as an extension.
- Language precedence is valid `?lang=`, then `Accept-Language`, then `en`; unsupported values fall back.
- Add a focused xUnit project for BuildingBlocks language and paging behavior.
## Out of scope
- P1 validation, authentication/authorization redesign, and removing the compatibility error shape.
## Follow-ups
- None.
