# ADR-010: Explicit mapping extensions or Mapperly, no reflection-based mappers

- Status: Accepted
- Date: 2026-10-14
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12)

## Context

Entity-to-DTO mapping today is done by hand with anonymous objects and explicit property assignment directly in Minimal API handlers (e.g. the response shapes in `AuthIdentityService/Program.cs`). This already avoids reflection; the decision is about keeping it that way as mapping surface grows rather than reaching for a runtime-reflection mapper.

## Decision

Map between entities and DTOs with explicit extension methods (e.g. `ToDto()`/`ToEntity()`) or Mapperly source-generated mappers where the mapping surface is large enough to justify generation. No runtime-reflection-based mapper.

## Rejected alternatives

- AutoMapper or similar runtime-reflection mappers: mapping mistakes surface at runtime instead of compile time, profiles are configured separately from the types they map, and reflection has a real per-call cost at the scale this API expects to reach.

## Consequences

- Mapping code is explicit and debuggable; a broken mapping is a compile error or an obvious line in a generated file, not a misconfigured profile discovered in production.
- Services with a large number of similar mappings adopt Mapperly rather than hand-writing every extension method.
